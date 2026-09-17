namespace FSharp.Interop.Dlr

open System
open System.Linq.Expressions
open System.Runtime.CompilerServices
open Microsoft.CSharp.RuntimeBinder
open FSharp.Quotations

/// Control-flow helpers the compiled block calls: `LeafExpressionConverter` cannot translate F# loop
/// or try nodes, but it can translate lambdas, so those become calls to these with the bodies as lambdas.
///
/// The bodies are plain delegates (`Func`, returning `unit` where there is nothing to return - a
/// quoted `unit` body is not `void`, so `Action` cannot take it), not F# functions. Quoting them as F# lambdas
/// would have `LeafExpressionConverter` wrap each compiled delegate in an `FSharpFunc` via
/// `FuncConvert`, and on Mono's browser-wasm runtime a non-capturing nested lambda invoked through
/// that wrapper loses its argument (a `try .. with` handler saw a null exception; `fun i -> i + 1`
/// returned 1) while the same delegate invoked directly is fine. The translator therefore emits
/// `NewDelegate` nodes, which convert to the delegate lambda itself with nothing in between.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
module DlrRuntime =
    /// `for x in items do body x`
    let forEach (items: seq<'T>) (body: Func<'T, unit>) : unit =
        for x in items do body.Invoke x

    /// `while guard () do body ()`
    let whileLoop (guard: Func<bool>) (body: Func<unit>) : unit =
        while guard.Invoke() do body.Invoke()

    /// `try body () with e -> handler e` (F# already puts the rethrow of unmatched exceptions in `handler`)
    let tryWith (body: Func<'T>) (handler: Func<exn, 'T>) : 'T =
        try body.Invoke() with e -> handler.Invoke e

    /// `try body () finally compensation ()`
    let tryFinally (body: Func<'T>) (compensation: Func<unit>) : 'T =
        try body.Invoke() finally compensation.Invoke()

    /// `use x = resource in body x`; a null resource is allowed, as in F#.
    let using (resource: 'R when 'R :> IDisposable) (body: Func<'R, 'T>) : 'T =
        try body.Invoke resource
        finally
            match box resource with
            | null -> ()
            | d -> (d :?> IDisposable).Dispose()

/// `x?Name(args)` where the member holds an F# function value rather than a delegate: C#'s
/// InvokeMember cannot invoke an FSharpFunc ("Cannot invoke a non-delegate type"), so when it
/// fails with a RuntimeBinderException the member is read and, if it is the `FSharpFunc` the
/// call's inferred type says it should be, applied; anything else rethrows the original error.
/// One overload per argument count; the try costs nothing on the successful path.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
[<AbstractClass; Sealed>]
type InvokeOrApply =
    static member private Rethrow(ex: exn) : 'T =
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
        Unchecked.defaultof<'T>

    static member Invoke<'R>(invoke: CallSite<Func<CallSite, obj, obj>>, get: CallSite<Func<CallSite, obj, obj>>, target: obj) : obj =
        try invoke.Target.Invoke(invoke, target)
        with :? Microsoft.CSharp.RuntimeBinder.RuntimeBinderException as ex ->
            match get.Target.Invoke(get, target) with
            | :? FSharpFunc<unit, 'R> as f -> box (f ())
            | _ -> InvokeOrApply.Rethrow ex

    static member Invoke<'A, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, obj>>, get: CallSite<Func<CallSite, obj, obj>>, target: obj, a: 'A) : obj =
        try invoke.Target.Invoke(invoke, target, a)
        with :? Microsoft.CSharp.RuntimeBinder.RuntimeBinderException as ex ->
            match get.Target.Invoke(get, target) with
            | :? FSharpFunc<'A, 'R> as f -> box (f a)
            | _ -> InvokeOrApply.Rethrow ex

    static member Invoke<'A, 'B, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, obj>>, get: CallSite<Func<CallSite, obj, obj>>, target: obj, a: 'A, b: 'B) : obj =
        try invoke.Target.Invoke(invoke, target, a, b)
        with :? Microsoft.CSharp.RuntimeBinder.RuntimeBinderException as ex ->
            match get.Target.Invoke(get, target) with
            | :? FSharpFunc<'A * 'B, 'R> as f -> box (f (a, b))
            | :? FSharpFunc<'A, FSharpFunc<'B, 'R>> as f -> box (f a b)
            | _ -> InvokeOrApply.Rethrow ex

    static member Invoke<'A, 'B, 'C, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, 'C, obj>>, get: CallSite<Func<CallSite, obj, obj>>, target: obj, a: 'A, b: 'B, c: 'C) : obj =
        try invoke.Target.Invoke(invoke, target, a, b, c)
        with :? Microsoft.CSharp.RuntimeBinder.RuntimeBinderException as ex ->
            match get.Target.Invoke(get, target) with
            | :? FSharpFunc<'A * 'B * 'C, 'R> as f -> box (f (a, b, c))
            | :? FSharpFunc<'A, FSharpFunc<'B, FSharpFunc<'C, 'R>>> as f -> box (f a b c)
            | _ -> InvokeOrApply.Rethrow ex

    static member Invoke<'A, 'B, 'C, 'D, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, 'C, 'D, obj>>, get: CallSite<Func<CallSite, obj, obj>>, target: obj, a: 'A, b: 'B, c: 'C, d: 'D) : obj =
        try invoke.Target.Invoke(invoke, target, a, b, c, d)
        with :? Microsoft.CSharp.RuntimeBinder.RuntimeBinderException as ex ->
            match get.Target.Invoke(get, target) with
            | :? FSharpFunc<'A * 'B * 'C * 'D, 'R> as f -> box (f (a, b, c, d))
            | :? FSharpFunc<'A, FSharpFunc<'B, FSharpFunc<'C, FSharpFunc<'D, 'R>>>> as f -> box (f a b c d)
            | _ -> InvokeOrApply.Rethrow ex

    // `Dlr.call`: the target itself may be the F# function.
    static member Call<'R>(invoke: CallSite<Func<CallSite, obj, obj>>, target: obj) : obj =
        try invoke.Target.Invoke(invoke, target)
        with :? Microsoft.CSharp.RuntimeBinder.RuntimeBinderException as ex ->
            match target with
            | :? FSharpFunc<unit, 'R> as f -> box (f ())
            | _ -> InvokeOrApply.Rethrow ex

    static member Call<'A, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, obj>>, target: obj, a: 'A) : obj =
        try invoke.Target.Invoke(invoke, target, a)
        with :? Microsoft.CSharp.RuntimeBinder.RuntimeBinderException as ex ->
            match target with
            | :? FSharpFunc<'A, 'R> as f -> box (f a)
            | _ -> InvokeOrApply.Rethrow ex

    static member Call<'A, 'B, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, obj>>, target: obj, a: 'A, b: 'B) : obj =
        try invoke.Target.Invoke(invoke, target, a, b)
        with :? Microsoft.CSharp.RuntimeBinder.RuntimeBinderException as ex ->
            match target with
            | :? FSharpFunc<'A * 'B, 'R> as f -> box (f (a, b))
            | :? FSharpFunc<'A, FSharpFunc<'B, 'R>> as f -> box (f a b)
            | _ -> InvokeOrApply.Rethrow ex

    static member Call<'A, 'B, 'C, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, 'C, obj>>, target: obj, a: 'A, b: 'B, c: 'C) : obj =
        try invoke.Target.Invoke(invoke, target, a, b, c)
        with :? Microsoft.CSharp.RuntimeBinder.RuntimeBinderException as ex ->
            match target with
            | :? FSharpFunc<'A * 'B * 'C, 'R> as f -> box (f (a, b, c))
            | :? FSharpFunc<'A, FSharpFunc<'B, FSharpFunc<'C, 'R>>> as f -> box (f a b c)
            | _ -> InvokeOrApply.Rethrow ex

    static member Call<'A, 'B, 'C, 'D, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, 'C, 'D, obj>>, target: obj, a: 'A, b: 'B, c: 'C, d: 'D) : obj =
        try invoke.Target.Invoke(invoke, target, a, b, c, d)
        with :? Microsoft.CSharp.RuntimeBinder.RuntimeBinderException as ex ->
            match target with
            | :? FSharpFunc<'A * 'B * 'C * 'D, 'R> as f -> box (f (a, b, c, d))
            | :? FSharpFunc<'A, FSharpFunc<'B, FSharpFunc<'C, FSharpFunc<'D, 'R>>>> as f -> box (f a b c d)
            | _ -> InvokeOrApply.Rethrow ex

/// A call site whose member name is only known at run time (`(?) x name` with `name` a variable):
/// one compiled, typed delegate per distinct name, made on first use from a quotation template
/// the translator built for the site, so after that first call a name costs one dictionary
/// lookup and behaves exactly like a literal.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type NameCache<'Delegate when 'Delegate :> Delegate>(template: string -> Expr) =
    let compiled = System.Collections.Concurrent.ConcurrentDictionary<string, 'Delegate>()

    member _.Get(name: string) : 'Delegate =
        compiled.GetOrAdd(name, fun n ->
            let linq = Microsoft.FSharp.Linq.RuntimeHelpers.LeafExpressionConverter.QuotationToExpression(template n) :?> LambdaExpression
            linq.Compile() :?> 'Delegate)

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
          Name: string }

    /// A dynamically typed argument: the binder dispatches on its runtime type.
    let dynamicArg (e: Expr) =
        { Expr = e; Type = typeof<obj>; Flags = CSharpArgumentInfoFlags.None; Name = null }

    /// A statically typed argument: the binder uses the quotation's type, as C# would.
    /// An `obj`-typed expression stays dynamic so F# callers get FSharp.Interop.Dynamic-like
    /// overload resolution on boxed values.
    let typedArg (e: Expr) =
        if e.Type = typeof<obj> then dynamicArg e
        else { Expr = e; Type = e.Type; Flags = CSharpArgumentInfoFlags.UseCompileTimeType; Name = null }

    let private withFlag (flag: CSharpArgumentInfoFlags) (arg: Arg) =
        // Spelled out with int locals: `|||` straight on the enum resolved to the dynamic
        // (throwing) FSharp.Core path when compiled against the FSharp.Core floor.
        let current: int = LanguagePrimitives.EnumToValue arg.Flags
        let added: int = LanguagePrimitives.EnumToValue flag
        { arg with Flags = LanguagePrimitives.EnumOfValue(current ||| added) }

    /// A literal argument: the binder applies C#'s constant conversions (an in-range `int`
    /// literal to `byte`, `0` to an enum, `null` to any reference type).
    let constant (arg: Arg) = withFlag CSharpArgumentInfoFlags.Constant arg

    let named (name: string) (arg: Arg) =
        { withFlag CSharpArgumentInfoFlags.NamedArgument arg with Name = name }

    let private argInfo (a: Arg) = CSharpArgumentInfo.Create(a.Flags, a.Name)

    /// Emits the call-site invocation for `binder` over `args`, returning `resultType`
    /// (`Void` for a discarded result, which yields an Action-shaped site).
    let siteCall (binder: CallSiteBinder) (args: Arg list) (resultType: Type) : Expr =
        let delegateType =
            Expression.GetDelegateType(Array.ofList (typeof<CallSite> :: [ for a in args -> a.Type ] @ [ resultType ]))
        let siteType = typedefof<CallSite<_>>.MakeGenericType delegateType
        let site = siteType.GetMethod("Create").Invoke(null, [| box binder |])
        let siteExpr = Expr.Value(site, siteType)
        let target = Expr.FieldGet(siteExpr, siteType.GetField("Target"))
        Expr.Call(target, delegateType.GetMethod("Invoke"), siteExpr :: [ for a in args -> a.Expr ])

    let getMember (context: Type) (name: string) (target: Arg) =
        siteCall (Binder.GetMember(CSharpBinderFlags.None, name, context, [ argInfo target ])) [ target ] typeof<obj>

    let setMember (context: Type) (name: string) (target: Arg) (value: Arg) =
        siteCall (Binder.SetMember(CSharpBinderFlags.None, name, context, [ argInfo target; argInfo value ])) [ target; value ] typeof<obj>

    /// A `CallSite<_>` for `binder` over `args`, as a `Value` node and its type.
    let private site (binder: CallSiteBinder) (args: Arg list) (resultType: Type) =
        let delegateType =
            Expression.GetDelegateType(Array.ofList (typeof<CallSite> :: [ for a in args -> a.Type ] @ [ resultType ]))
        let siteType = typedefof<CallSite<_>>.MakeGenericType delegateType
        Expr.Value(siteType.GetMethod("Create").Invoke(null, [| box binder |]), siteType)

    let invokeMember (context: Type) (name: string) (typeArgs: Type list) (discard: bool) (target: Arg) (args: Arg list) =
        let flags = if discard then CSharpBinderFlags.ResultDiscarded else CSharpBinderFlags.None
        let all = target :: args
        let typeArgs = match typeArgs with [] -> null | ts -> ts :> seq<Type>
        let binder = Binder.InvokeMember(flags, name, typeArgs, context, [ for a in all -> argInfo a ])
        siteCall binder all (if discard then voidType else typeof<obj>)

    /// `x?Name(args)` whose inferred type is `A -> R`: InvokeMember, falling back to applying an
    /// F# function value held by the member (see InvokeOrApply). Positional, non-generic calls with
    /// up to four arguments; anything else is a plain InvokeMember.
    let invokeMemberOrApply (context: Type) (name: string) (typeArgs: Type list) (discard: bool) (resultType: Type) (target: Arg) (args: Arg list) =
        let positional = args |> List.forall (fun a -> isNull a.Name)
        if not positional || not typeArgs.IsEmpty || args.Length > 4 || discard then
            invokeMember context name typeArgs discard target args
        else
            let all = target :: args
            let invokeSite = site (Binder.InvokeMember(CSharpBinderFlags.None, name, null, context, [ for a in all -> argInfo a ])) all typeof<obj>
            let getSite = site (Binder.GetMember(CSharpBinderFlags.None, name, context, [ argInfo target ])) [ target ] typeof<obj>
            let helper =
                typeof<InvokeOrApply>.GetMethods()
                |> Array.find (fun m -> m.Name = "Invoke" && m.GetGenericArguments().Length = args.Length + 1)
                |> fun m -> m.MakeGenericMethod(Array.ofList ([ for a in args -> a.Type ] @ [ resultType ]))
            Expr.Call(helper, invokeSite :: getSite :: target.Expr :: [ for a in args -> a.Expr ])

    let invoke (context: Type) (discard: bool) (target: Arg) (args: Arg list) =
        let flags = if discard then CSharpBinderFlags.ResultDiscarded else CSharpBinderFlags.None
        let all = target :: args
        let binder = Binder.Invoke(flags, context, [ for a in all -> argInfo a ])
        siteCall binder all (if discard then voidType else typeof<obj>)

    /// `Dlr.call args target` with a fallback to applying `target` as an F# function of the
    /// arguments' types (see InvokeOrApply.Call).
    let invokeOrApply (context: Type) (discard: bool) (resultType: Type) (target: Arg) (args: Arg list) =
        let positional = args |> List.forall (fun a -> isNull a.Name)
        if not positional || args.Length > 4 || discard then invoke context discard target args
        else
            let all = target :: args
            let invokeSite = site (Binder.Invoke(CSharpBinderFlags.None, context, [ for a in all -> argInfo a ])) all typeof<obj>
            let helper =
                typeof<InvokeOrApply>.GetMethods()
                |> Array.find (fun m -> m.Name = "Call" && m.GetGenericArguments().Length = args.Length + 1)
                |> fun m -> m.MakeGenericMethod(Array.ofList ([ for a in args -> a.Type ] @ [ resultType ]))
            Expr.Call(helper, invokeSite :: target.Expr :: [ for a in args -> a.Expr ])

    let getIndex (context: Type) (target: Arg) (indexes: Arg list) =
        let all = target :: indexes
        siteCall (Binder.GetIndex(CSharpBinderFlags.None, context, [ for a in all -> argInfo a ])) all typeof<obj>

    let setIndex (context: Type) (target: Arg) (indexes: Arg list) (value: Arg) =
        let all = target :: indexes @ [ value ]
        siteCall (Binder.SetIndex(CSharpBinderFlags.None, context, [ for a in all -> argInfo a ])) all typeof<obj>

    /// C#'s `d.Name += v` / `-=`: an IsEvent site decides at run time between the event
    /// accessor (`add_Name`/`remove_Name`, invoked as a special name) and read-modify-write
    /// (GetMember, AddAssign/SubtractAssign, SetMember flagged as a compound assignment).
    /// `target` and `value` must be variables, since both branches mention them.
    let compoundAssign (context: Type) (name: string) (subtract: bool) (target: Arg) (value: Arg) : Expr =
        let isEvent =
            siteCall (Binder.IsEvent(CSharpBinderFlags.None, name, context)) [ target ] typeof<bool>
        let accessor =
            let binder =
                Binder.InvokeMember(
                    CSharpBinderFlags.InvokeSpecialName ||| CSharpBinderFlags.ResultDiscarded,
                    (if subtract then "remove_" else "add_") + name, null, context, [ argInfo target; argInfo value ])
            siteCall binder [ target; value ] voidType
        let readModifyWrite =
            let current = siteCall (Binder.GetMember(CSharpBinderFlags.None, name, context, [ argInfo target ])) [ target ] typeof<obj>
            let op = if subtract then ExpressionType.SubtractAssign else ExpressionType.AddAssign
            let combined =
                siteCall (Binder.BinaryOperation(CSharpBinderFlags.None, op, context, [ argInfo (dynamicArg current); argInfo value ]))
                    [ dynamicArg current; value ] typeof<obj>
            let set =
                Binder.SetMember(CSharpBinderFlags.ValueFromCompoundAssignment, name, context, [ argInfo target; argInfo (dynamicArg combined) ])
            siteCall set [ target; dynamicArg combined ] typeof<obj>
        Expr.IfThenElse(isEvent, Expr.Sequential(accessor, Expr.Value(())), Expr.Sequential(readModifyWrite, Expr.Value(())))

    let binaryOperation (context: Type) (op: ExpressionType) (left: Arg) (right: Arg) =
        let binder = Binder.BinaryOperation(CSharpBinderFlags.None, op, context, [ argInfo left; argInfo right ])
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
