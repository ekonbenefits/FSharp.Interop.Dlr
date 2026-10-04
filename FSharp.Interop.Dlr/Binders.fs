namespace FSharp.Interop.Dlr

open System
open System.Dynamic
open System.Linq.Expressions
open System.Reflection
open System.Runtime.CompilerServices
open Microsoft.CSharp.RuntimeBinder
open FSharp.Quotations

/// Builds Microsoft.CSharp binders and emits the quotation fragment that calls a
/// pre-created CallSite: `Call(FieldGet(Value site, Target), Invoke, site :: args)`.
/// The `Value site` becomes an Expression.Constant, so the site is baked into the
/// compiled delegate.
module internal Binders =

    /// `voidType` makes the F# compiler emit IL the JIT rejects, so get it indirectly.
    let private voidType = typeof<Action>.GetMethod("Invoke").ReturnType

    /// One argument as the binder sees it: its expression, the static type used in
    /// the site delegate, and the C# argument-info flags.
    type Arg =
        { Expr: Expr
          Type: Type
          Flags: CSharpArgumentInfoFlags
          Name: string
          /// A `Dlr.ref`'s variable: two refs over the same one are one storage at the call, as in C#.
          Alias: obj }

    /// A dynamically typed argument: the binder dispatches on its runtime type.
    let dynamicArg (e: Expr) =
        { Expr = e; Type = typeof<obj>; Flags = CSharpArgumentInfoFlags.None; Name = null; Alias = null }

    /// A type as the target (`Dlr.Static<T>.Overloads`, `Dlr.new'<T>`): argument 0 of the site is
    /// `typeof<T>` flagged as a static type, the C# compiler's shape for `T.Member(…)`.
    let staticTarget (t: Type) =
        { Expr = Expr.Value(t, typeof<Type>); Type = typeof<Type>; Flags = CSharpArgumentInfoFlags.UseCompileTimeType ||| CSharpArgumentInfoFlags.IsStaticType; Name = null; Alias = null }

    let isStatic (a: Arg) = a.Flags.HasFlag CSharpArgumentInfoFlags.IsStaticType

    /// `Dlr.Static<T>.Overloads` is for calls; C#'s binder has no static form of the other operations
    /// (GetMember, SetMember, IsEvent, indexers) and plain F# already has them: `T.P`.
    let private callsOnly (what: string) (target: Arg) =
        if isStatic target then
            raise (DlrTranslationException(sprintf "dlr { } does not support %s on Dlr.Static<T>.Overloads, which is for calls only: a static property or field is `T.P` in plain F#." what))

    /// A statically typed argument: the binder uses the quotation's type, as C# would.
    /// An `obj`-typed expression stays dynamic so F# callers get FSharp.Interop.Dynamic-like
    /// overload resolution on boxed values.
    let typedArg (e: Expr) =
        if e.Type = typeof<obj> then dynamicArg e
        else { Expr = e; Type = e.Type; Flags = CSharpArgumentInfoFlags.UseCompileTimeType; Name = null; Alias = null }

    let private withFlag (flag: CSharpArgumentInfoFlags) (arg: Arg) =
        // Spelled out with int locals: `|||` straight on the enum resolved to the dynamic
        // (throwing) FSharp.Core path when compiled against the FSharp.Core floor.
        let current: int = LanguagePrimitives.EnumToValue arg.Flags
        let added: int = LanguagePrimitives.EnumToValue flag
        { arg with Flags = LanguagePrimitives.EnumOfValue(current ||| added) }

    /// A literal argument: the binder applies C#'s constant conversions (an in-range `int`
    /// literal to `byte`, `0` to an enum, `null` to any reference type).
    let constant (arg: Arg) = withFlag CSharpArgumentInfoFlags.Constant arg

    /// A `ref` / `out` argument (`Dlr.ref v`, `Dlr.out`): the site's parameter is `t&`, and
    /// `initial` is the value passed in (null for an out); `alias` is a ref's variable (null for an out).
    let byRefArg (isOut: bool) (t: Type) (initial: Expr) (alias: obj) =
        withFlag (if isOut then CSharpArgumentInfoFlags.IsOut else CSharpArgumentInfoFlags.IsRef)
            { Expr = initial; Type = t.MakeByRefType(); Flags = CSharpArgumentInfoFlags.UseCompileTimeType; Name = null; Alias = alias }

    let named (name: string) (arg: Arg) =
        { withFlag CSharpArgumentInfoFlags.NamedArgument arg with Name = name }

    let private argInfo (a: Arg) = CSharpArgumentInfo.Create(a.Flags, a.Name)

    /// C#'s flags for a call whose result is discarded (a statement) or used.
    let private resultFlags (discard: bool) = if discard then CSharpBinderFlags.ResultDiscarded else CSharpBinderFlags.None

    /// No argument is named: the positional F# rules apply (a named call is C#'s alone).
    let private allPositional (args: Arg list) = args |> List.forall (fun a -> isNull a.Name)

    /// A `CallSite<_>` for `binder` over `args`, as a `Value` node (a constant in the compiled tree).
    let private site (binder: CallSiteBinder) (args: Arg list) (resultType: Type) =
        let delegateType =
            Expression.GetDelegateType(Array.ofList (typeof<CallSite> :: [ for a in args -> a.Type ] @ [ resultType ]))
        let siteType = typedefof<CallSite<_>>.MakeGenericType delegateType
        Expr.Value(siteType.GetMethod("Create").Invoke(null, [| box binder |]), siteType)

    /// An in-place operation on a struct copy, then its write-back (`inPlace` in Translate): the
    /// write-back must run even when the operation throws after mutating, as plain F# keeps the
    /// mutation — a `finally`, which the quotation converter has no form for. This placeholder is
    /// what the translator emits; the LINQ `SiteHoister` rewrites it into a TryFinally. The body is
    /// the slow path, for a tree the hoister has not seen (the write-back then only on success).
    type InPlace =
        static member Then<'T>(operation: 'T, writeBack: unit) : 'T = ignore writeBack; operation
    /// Past Func's 17 type parameters (15 arguments and up) a site's delegate is a type emitted at run time,
    /// which must not be named in a quotation: FSharp.Core's checks ask its assembly
    /// `ReflectionOnly`, unimplemented on browser-wasm. Such a call is written as one of these
    /// placeholders — every type in it a plain one — and the LINQ `SiteHoister` in Translate
    /// rewrites it into the typed `Invoke` after conversion. The bodies are the slow path, for a
    /// tree the hoister has not seen.
    let private wideInvoke (site: CallSite) (args: obj[]) =
        let target = site.GetType().GetField("Target").GetValue site :?> Delegate
        target.DynamicInvoke(Array.append [| box site |] args)
    type WideSite =
        static member Invoke(site: CallSite, delegateType: Type, args: obj[]) : obj = ignore delegateType; wideInvoke site args
        static member InvokeVoid(site: CallSite, delegateType: Type, args: obj[]) : unit = ignore delegateType; wideInvoke site args |> ignore
    let private wideMethod (name: string) = typeof<WideSite>.GetMethod(name, BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
    let private wideInvokeMethod = wideMethod "Invoke"
    let private wideInvokeVoidMethod = wideMethod "InvokeVoid"

    /// The holder a byref call returns: the result (`obj`), then each byref argument's value after
    /// the call, typed, in argument order — a `ValueTuple`, nested in its `Rest` past seven, so
    /// nothing is boxed or allocated on the way out.
    let byRefHolderType (types: Type list) : Type = Tuples.valueTupleOf types
    /// Element `i` of a holder: `Item(i+1)`, through `Rest` past the seventh.
    let byRefHolderPath (holder: Type) (i: int) : FieldInfo list = Tuples.fieldPath holder i

    /// A site with byref parameters (`Dlr.out`, `Dlr.ref`). No quotation can pass a byref, so the
    /// call is this placeholder: the values go in as an `obj[]` (target first) and come back as the
    /// holder `'H` (see `byRefHolderType`). `byRefs` are the byref positions in `args`, `outs` those
    /// of them that are out (whose value in is not read: it starts as the default), and `sameAs`
    /// per position the earlier one over the same variable (-1 if none), which shares its storage. The LINQ
    /// `SiteHoister` rewrites it into the typed `Invoke` (no array, no boxing); this body is the
    /// slow path, correct on the JIT (`DynamicInvoke` writes byref parameters back into its array)
    /// but not on Mono wasm, which is why the rewrite is required there.
    type ByRefSite =
        static member Invoke<'H>(site: CallSite, delegateType: Type, args: obj[], byRefs: int[], outs: int[], sameAs: int[]) : 'H =
            ignore (delegateType, outs, sameAs)
            let target = site.GetType().GetField("Target").GetValue site :?> Delegate
            let all = Array.append [| box site |] args
            let result =
                // A binder's error, or the callee's, arrives as itself, as at any other site.
                try target.DynamicInvoke all
                with :? TargetInvocationException as e when not (isNull e.InnerException) ->
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw()
                    null
            let values = result :: [ for i in byRefs -> all.[i + 1] ]
            Tuples.make typeof<'H> values :?> 'H
    let private byRefInvokeMethod = typeof<ByRefSite>.GetMethod("Invoke", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)

    /// The holder type of a byref call over `all` (target first): the result, then each byref's type.
    let byRefHolderOf (all: Arg list) = byRefHolderType (typeof<obj> :: [ for a in all do if a.Type.IsByRef then yield a.Type.GetElementType() ])

    /// A byref site (see `ByRefSite`) for `binder` over `all`, returning `resultType` (`Void` for a
    /// discarded result); the expression is the holder of result and byref values.
    let private byRefSite (binder: CallSiteBinder) (all: Arg list) (resultType: Type) : Expr =
        let delegateType = Expression.GetDelegateType(Array.ofList (typeof<CallSite> :: [ for a in all -> a.Type ] @ [ resultType ]))
        let site = typedefof<CallSite<_>>.MakeGenericType(delegateType).GetMethod("Create").Invoke(null, [| box binder |])
        let byRefs = [| for i, a in List.indexed all do if a.Type.IsByRef then yield i |]
        let outs = [| for i, a in List.indexed all do if a.Flags.HasFlag CSharpArgumentInfoFlags.IsOut then yield i |]
        // Per position: the first earlier position whose ref is over the same variable, else -1.
        let sameAs =
            [| for i, a in List.indexed all ->
                if isNull a.Alias then -1
                else match all |> List.take i |> List.tryFindIndex (fun b -> obj.ReferenceEquals(b.Alias, a.Alias)) with Some j -> j | None -> -1 |]
        let holder = byRefHolderOf all
        let boxed = [ for a in all -> if a.Expr.Type = typeof<obj> then a.Expr else Expr.Coerce(a.Expr, typeof<obj>) ]
        Expr.Call(byRefInvokeMethod.MakeGenericMethod holder,
                  [ Expr.Value(site, typeof<CallSite>); Expr.Value(delegateType, typeof<Type>)
                    Expr.NewArray(typeof<obj>, boxed); Expr.Value(byRefs); Expr.Value(outs); Expr.Value(sameAs) ])

    /// `x?M(…, Dlr.out, Dlr.ref v, …)`: C#'s InvokeMember over byref parameters (target first in `all`).
    let invokeMemberByRef (context: Type) (name: string) (typeArgs: Type list) (discard: bool) (all: Arg list) : Expr =
        let flags = resultFlags discard
        let typeArgSeq = match typeArgs with [] -> null | ts -> ts :> seq<Type>
        byRefSite (Binder.InvokeMember(flags, name, typeArgSeq, context, [ for a in all -> argInfo a ])) all (if discard then voidType else typeof<obj>)

    /// `Dlr.call f (…, Dlr.out, …)` / `Dlr.apply`: C#'s Invoke of the value itself over byref parameters.
    let invokeByRef (context: Type) (discard: bool) (all: Arg list) : Expr =
        callsOnly "invoking a value (Dlr.call / Dlr.apply)" (List.head all)
        let flags = resultFlags discard
        byRefSite (FSharpByRefInvokeBinder(Binder.Invoke(flags, context, [ for a in all -> argInfo a ]) :?> InvokeBinder, discard, [| for a in List.tail all -> a.Flags |])) all (if discard then voidType else typeof<obj>)

    /// `Dlr.new'<T>(…, Dlr.out, …)`: C#'s InvokeConstructor over byref parameters; the result is typed `t`.
    let invokeConstructorByRef (context: Type) (t: Type) (args: Arg list) : Expr =
        let all = staticTarget t :: args
        byRefSite (Binder.InvokeConstructor(CSharpBinderFlags.None, context, [ for a in all -> argInfo a ])) all t

    /// Emits the call-site invocation for `binder` over `args`, returning `resultType`
    /// (`Void` for a discarded result, which yields an Action-shaped site).
    let siteCall (binder: CallSiteBinder) (args: Arg list) (resultType: Type) : Expr =
        let siteExpr = site binder args resultType
        let siteValue = match siteExpr with FSharp.Quotations.Patterns.Value(v, _) -> v | _ -> null
        let siteType = siteExpr.Type
        let siteDelegate = siteType.GetGenericArguments().[0]
        // `args` holds the target too; the site's delegate takes the CallSite before them.
        if DelegateMembers.funcFits (args.Length + 1) then
            let target = Expr.FieldGet(siteExpr, siteType.GetField("Target"))
            Expr.Call(target, siteDelegate.GetMethod("Invoke"), siteExpr :: [ for a in args -> a.Expr ])
        else
            let isVoid = resultType = typeof<Action>.GetMethod("Invoke").ReturnType
            let boxed = [ for a in args -> if a.Expr.Type = typeof<obj> then a.Expr else Expr.Coerce(a.Expr, typeof<obj>) ]
            let call = Expr.Call((if isVoid then wideInvokeVoidMethod else wideInvokeMethod),
                                 [ Expr.Value(siteValue, typeof<CallSite>); Expr.Value(siteDelegate, typeof<Type>); Expr.NewArray(typeof<obj>, boxed) ])
            if isVoid || resultType = typeof<obj> then call else Expr.Coerce(call, resultType)

    let getMember (context: Type) (name: string) (target: Arg) =
        callsOnly "reading a member" target
        siteCall (Binder.GetMember(CSharpBinderFlags.None, name, context, [ argInfo target ])) [ target ] typeof<obj>

    let setMember (context: Type) (name: string) (target: Arg) (value: Arg) =
        callsOnly "setting a member" target
        let csharp = Binder.SetMember(CSharpBinderFlags.None, name, context, [ argInfo target; argInfo value ]) :?> SetMemberBinder
        siteCall (MetaObjectAwareBinder(FSharpSetMemberBinder(context, name, csharp))) [ target; value ] typeof<obj>

    /// C#'s InvokeMember binder wrapped to apply F# function values (see FSharpInvokeMemberBinder)
    /// for positional, non-generic calls of any arity; otherwise C#'s binder as is.
    let private smartInvokeMember (context: Type) (name: string) (typeArgs: Type list) (discard: bool) (all: Arg list) : CallSiteBinder =
        let args = List.tail all
        let flags = resultFlags discard
        let typeArgSeq = match typeArgs with [] -> null | ts -> ts :> seq<Type>
        let csharp = Binder.InvokeMember(flags, name, typeArgSeq, context, [ for a in all -> argInfo a ])
        let positional = allPositional args
        // A static target has no instance for the function-member rules, but the argument rules
        // (optional parameters, function/delegate conversions) apply to its static methods.
        if isStatic (List.head all) then
            (if not positional || not typeArgs.IsEmpty then csharp else FSharpStaticInvokeMemberBinder(context, name, csharp :?> InvokeMemberBinder) :> CallSiteBinder)
        else
            // Discarded results too: the site is void-returning and the DLR drops the rule's value.
            let csharpInvoke = Binder.Invoke(flags, context, [ for a in all -> argInfo a ]) :?> InvokeBinder
            let inner = if not positional || not typeArgs.IsEmpty then csharp else FSharpInvokeMemberBinder(context, name, csharp :?> InvokeMemberBinder, csharpInvoke) :> CallSiteBinder
            // An instance target may be a meta-object: F# function arguments become delegates for it.
            MetaObjectAwareBinder(inner :?> DynamicMetaObjectBinder) :> CallSiteBinder

    /// `x?Name(args)` whose inferred type is `A -> R`: InvokeMember, applying an F# function value
    /// held by the member when C# cannot invoke it.
    let invokeMemberOrApply (context: Type) (name: string) (typeArgs: Type list) (discard: bool) (target: Arg) (args: Arg list) =
        let all = target :: args
        siteCall (smartInvokeMember context name typeArgs discard all) all (if discard then voidType else typeof<obj>)

    /// `Dlr.new'<T>(args)`: C#'s `new T(…)` with the constructor chosen by the arguments'
    /// runtime types. The type goes in as argument 0 of the site, flagged as a static type,
    /// exactly as the C# compiler emits it.
    let invokeConstructor (context: Type) (t: Type) (args: Arg list) =
        let all = staticTarget t :: args
        // Result typed `t`, as the C# compiler's own site for `new T(…)` is: the binder types a
        // constructor's result as `T`, and an obj-typed site would reject that for a value type.
        let csharp = Binder.InvokeConstructor(CSharpBinderFlags.None, context, [ for a in all -> argInfo a ]) :?> DynamicMetaObjectBinder
        let positional = allPositional args
        let binder = if positional then FSharpInvokeConstructorBinder(context, t, csharp) :> CallSiteBinder else csharp :> CallSiteBinder
        siteCall binder all t

    /// `Dlr.call target args` / `Dlr.apply args target`: invoke `target` itself, applying it when
    /// it is an F# function.
    let invokeOrApply (context: Type) (discard: bool) (target: Arg) (args: Arg list) =
        callsOnly "invoking a value (Dlr.call / Dlr.apply)" target
        let all = target :: args
        let csharp = Binder.Invoke(resultFlags discard, context, [ for a in all -> argInfo a ])
        let positional = allPositional args
        let binder =
            if not positional then csharp
            else FSharpInvokeBinder(csharp :?> InvokeBinder) :> CallSiteBinder
        siteCall (MetaObjectAwareBinder(binder :?> DynamicMetaObjectBinder)) all (if discard then voidType else typeof<obj>)

    /// `asFunction`'s factories past five, per (function type, invoke site type).
    let private factories = System.Collections.Concurrent.ConcurrentDictionary<struct (Type * Type), Delegate>(TypePairComparer.Instance)

    /// A value read as an F# function type (see FunctionMember): the argument types come from the
    /// function type's domains, curried or tupled; `binderFor` gives the site's binder for those
    /// typed argument slots (`unit -> R` gets an empty list), and `shortcut` says to return the
    /// target itself when it already is a function of that type (a bare value, not a member read).
    /// Any arity, curried or tupled: typed helpers up to five, functions built at run time past it.
    let private asFunction (context: Type) (functionType: Type) (original: Arg) (shortcut: bool) (binderFor: Arg list -> bool -> CallSiteBinder) : Expr =
        let target = original
        let rec domains (t: Type) =
            if FSharp.Reflection.FSharpType.IsFunction t then
                let d, r = FSharp.Reflection.FSharpType.GetFunctionElements t
                let ds, result = domains r
                d :: ds, result
            else [], t
        let ds, resultType = domains functionType
        let tupled, argTypes =
            match ds with
            | [ d ] when d = typeof<unit> -> false, []
            | [ d ] when FSharp.Reflection.FSharpType.IsTuple d -> true, List.ofArray (FSharp.Reflection.FSharpType.GetTupleElements d)
            | ds -> false, ds

        // With the shortcut the target is evaluated once into a variable that both the type test
        // and the invoker read.
        let targetVar = Var("target", typeof<obj>)
        let target = if shortcut then { target with Expr = Expr.Var targetVar } else target
        let invokeArgs = [ for t in argTypes -> typedArg (Expr.Value(null, t)) ]
        let all = target :: invokeArgs
        // `… -> unit` invokes with the result discarded (a void site), as a statement call does.
        let discard = resultType = typeof<unit>
        let invokeSite = site (binderFor all discard) all (if discard then voidType else typeof<obj>)
        let shape = (if tupled then "Tupled" else "Curried") + string argTypes.Length
        let built =
            if argTypes.Length > 5 then
                // Beyond the typed helpers, the function as a factory compiled once here (see
                // FunctionBuilder): the sites and the target in, the function over them out. The
                // sites stay in the quotation, as `CallSite` constants (a per-key site lifts them),
                // typed as the base: a wide site's delegate type is emitted, which a quotation
                // must not name (see WideSite).
                let sites =
                    [ yield invokeSite
                      if not discard then yield site (Binder.Convert(CSharpBinderFlags.None, resultType, context)) [ dynamicArg (Expr.Value(null, typeof<obj>)) ] resultType ]
                    |> List.map (function Patterns.Value(v, t) -> v, t | _ -> failwith "unreachable")
                let factoryType = Expression.GetFuncType(Array.ofList ([ for _ in sites -> typeof<CallSite> ] @ [ typeof<obj>; functionType ]))
                // The factory depends on the function type and the sites' types, not on the sites:
                // compiled once per shape, which a per-key site's template (built again per key, for
                // its sites) then finds here rather than compiling and discarding its own.
                let factory =
                    factories.GetOrAdd(struct (functionType, snd sites.Head), fun _ ->
                        let siteParams = [ for i in 0 .. sites.Length - 1 -> Expression.Parameter(typeof<CallSite>, sprintf "site%d" i) ]
                        let targetParam = Expression.Parameter(typeof<obj>, "target")
                        // Captured: each site as its own type, then the target.
                        let captured = [ for p, (_, t) in List.zip siteParams sites -> Expression.Convert(p, t) :> Expression ] @ [ targetParam ]
                        let finish (captured: Expression list) (args: Expression list) =
                            let invoke (i: int) (args: Expression list) =
                                Expression.Invoke(Expression.Field(captured.[i], "Target"), captured.[i] :: args) :> Expression
                            let raw = invoke 0 (List.last captured :: args)
                            if discard then FunctionBuilder.unitOf raw else invoke 1 [ raw ]
                        let body = FunctionBuilder.build (if tupled then Some (List.head ds) else None) argTypes (if discard then typeof<unit> else resultType) captured finish
                        Expression.Lambda(factoryType, body, siteParams @ [ targetParam ]).Compile())
                Expr.Call(Expr.Value(factory, factoryType), factoryType.GetMethod("Invoke"),
                          [ for v, _ in sites -> Expr.Value(v, typeof<CallSite>) ] @ [ target.Expr ])
            elif discard then
                let helper = typeof<FunctionMember>.GetMethod(shape + "Unit")
                let helper = if argTypes.IsEmpty then helper else helper.MakeGenericMethod(Array.ofList argTypes)
                Expr.Call(helper, [ invokeSite; target.Expr ])
            else
                let convertSite = site (Binder.Convert(CSharpBinderFlags.None, resultType, context)) [ dynamicArg (Expr.Value(null, typeof<obj>)) ] resultType
                let helper = typeof<FunctionMember>.GetMethod(shape).MakeGenericMethod(Array.ofList (argTypes @ [ resultType ]))
                Expr.Call(helper, [ invokeSite; convertSite; target.Expr ])
        if shortcut then
            // A value that already is a function of this type is that function: no invoker.
            Expr.Let(targetVar, original.Expr,
                     Expr.IfThenElse(Expr.TypeTest(Expr.Var targetVar, functionType), Expr.Coerce(Expr.Var targetVar, functionType), built))
        else built

    /// `x?Name` read as an F# function type: `unit -> R` reads a property or invokes a
    /// parameterless method; otherwise a typed InvokeMember site.
    let functionMember (context: Type) (name: string) (functionType: Type) (target: Arg) : Expr =
        callsOnly "reading a member as a function" target
        asFunction context functionType target false (fun all discard ->
            let flags = resultFlags discard
            if all.Length = 1 then
                let csharp = Binder.InvokeMember(flags, name, null, context, [ argInfo target ]) :?> InvokeMemberBinder
                let csharpInvoke = Binder.Invoke(flags, context, [ argInfo target ]) :?> InvokeBinder
                FSharpReadOrInvokeBinder(context, name, csharp, csharpInvoke) :> CallSiteBinder
            else smartInvokeMember context name [] discard all)

    /// `Dlr.call x` read as an F# function type: the target itself as that function — a typed
    /// Invoke site (F# function values through FSharpInvokeBinder), or the target as it is when
    /// it already is a function of the type.
    let functionTarget (context: Type) (functionType: Type) (target: Arg) : Expr =
        callsOnly "Dlr.call" target
        asFunction context functionType target true (fun all discard ->
            let flags = resultFlags discard
            FSharpInvokeBinder(Binder.Invoke(flags, context, [ for a in all -> argInfo a ]) :?> InvokeBinder) :> CallSiteBinder)

    let getIndex (context: Type) (target: Arg) (indexes: Arg list) =
        callsOnly "indexing" target
        let all = target :: indexes
        siteCall (Binder.GetIndex(CSharpBinderFlags.None, context, [ for a in all -> argInfo a ])) all typeof<obj>

    let setIndex (context: Type) (target: Arg) (indexes: Arg list) (value: Arg) =
        callsOnly "indexing" target
        let all = target :: indexes @ [ value ]
        // C#'s SetIndex binder, our value conversion as its error suggestion; a meta-object target
        // with an F# function value gets the delegate.
        let csharp = Binder.SetIndex(CSharpBinderFlags.None, context, [ for a in all -> argInfo a ]) :?> SetIndexBinder
        siteCall (MetaObjectAwareBinder(FSharpSetIndexBinder(context, csharp))) all typeof<obj>

    /// C#'s `d.Name += v` / `-=`: an IsEvent site decides at run time between the event
    /// accessor (`add_Name`/`remove_Name`, invoked as a special name) and read-modify-write
    /// (GetMember, AddAssign/SubtractAssign, SetMember flagged as a compound assignment).
    /// `target` and `value` must be variables, since both branches mention them. An F# function
    /// value converts on both: to the event's delegate type at the accessor (the invoke-member
    /// seam), and to the delegate of its signature for a meta-object's member (a COM event's
    /// bound event takes delegates only), through `MetaObjectAwareBinder`.
    let compoundAssign (context: Type) (name: string) (subtract: bool) (target: Arg) (value: Arg) : Expr =
        callsOnly "addAssign/subtractAssign" target
        let isEvent =
            siteCall (Binder.IsEvent(CSharpBinderFlags.None, name, context)) [ target ] typeof<bool>
        let accessor =
            let accessorName = (if subtract then "remove_" else "add_") + name
            let infos = [ argInfo target; argInfo value ]
            let csharp =
                Binder.InvokeMember(CSharpBinderFlags.InvokeSpecialName ||| CSharpBinderFlags.ResultDiscarded, accessorName, null, context, infos)
            let csharpInvoke = Binder.Invoke(CSharpBinderFlags.ResultDiscarded, context, infos) :?> InvokeBinder
            siteCall (FSharpInvokeMemberBinder(context, accessorName, csharp :?> InvokeMemberBinder, csharpInvoke)) [ target; value ] voidType
        let readModifyWrite =
            let current = siteCall (Binder.GetMember(CSharpBinderFlags.None, name, context, [ argInfo target ])) [ target ] typeof<obj>
            let op = if subtract then ExpressionType.SubtractAssign else ExpressionType.AddAssign
            let combined =
                let csharp = Binder.BinaryOperation(CSharpBinderFlags.None, op, context, [ argInfo (dynamicArg current); argInfo value ])
                siteCall (MetaObjectAwareBinder(csharp :?> DynamicMetaObjectBinder)) [ dynamicArg current; value ] typeof<obj>
            let set =
                Binder.SetMember(CSharpBinderFlags.ValueFromCompoundAssignment, name, context, [ argInfo target; argInfo (dynamicArg combined) ])
            siteCall set [ target; dynamicArg combined ] typeof<obj>
        Expr.IfThenElse(isEvent, Expr.Sequential(accessor, Expr.Value(())), Expr.Sequential(readModifyWrite, Expr.Value(())))

    let binaryOperation (context: Type) (op: ExpressionType) (left: Arg) (right: Arg) =
        let csharp = Binder.BinaryOperation(CSharpBinderFlags.None, op, context, [ argInfo left; argInfo right ]) :?> BinaryOperationBinder
        let binder = if FSharpBinaryOperationBinder.IsComparison op then FSharpBinaryOperationBinder csharp :> CallSiteBinder else csharp :> CallSiteBinder
        siteCall binder [ left; right ] typeof<obj>

    let unaryOperation (context: Type) (op: ExpressionType) (operand: Arg) =
        let binder = Binder.UnaryOperation(CSharpBinderFlags.None, op, context, [ argInfo operand ])
        siteCall binder [ operand ] typeof<obj>

    /// Implicit conversion of an `obj`-typed expression to `resultType`. `obj` is a no-op and
    /// `unit` discards the value.
    let convert (context: Type) (resultType: Type) (e: Expr) : Expr =
        if resultType = typeof<obj> then e
        elif resultType = typeof<unit> then Expr.Sequential(e, Expr.Value(()))
        else
            let binder = Binder.Convert(CSharpBinderFlags.None, resultType, context)
            siteCall binder [ dynamicArg e ] resultType

    /// Explicit conversion (a C# cast) of an `obj`-typed expression to `resultType`.
    let convertExplicit (context: Type) (resultType: Type) (e: Expr) : Expr =
        let binder = Binder.Convert(CSharpBinderFlags.ConvertExplicit, resultType, context)
        siteCall binder [ dynamicArg e ] resultType
