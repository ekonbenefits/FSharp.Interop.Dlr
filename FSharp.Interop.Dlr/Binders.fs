namespace FSharp.Interop.Dlr

open System
open System.Dynamic
open System.Linq.Expressions
open System.Reflection
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

/// Applying an F# function value with the arguments of a call site: one overload per shape the
/// call's inferred type `A -> B -> R` can name, tupled or curried.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
[<AbstractClass; Sealed>]
type Apply =
    static member Unit<'R>(f: FSharpFunc<unit, 'R>) : obj = box (f ())
    static member One<'A, 'R>(f: FSharpFunc<'A, 'R>, a: 'A) : obj = box (f a)
    static member Tupled2<'A, 'B, 'R>(f: FSharpFunc<'A * 'B, 'R>, a: 'A, b: 'B) : obj = box (f (a, b))
    static member Curried2<'A, 'B, 'R>(f: FSharpFunc<'A, FSharpFunc<'B, 'R>>, a: 'A, b: 'B) : obj = box (f a b)
    static member Tupled3<'A, 'B, 'C, 'R>(f: FSharpFunc<'A * 'B * 'C, 'R>, a: 'A, b: 'B, c: 'C) : obj = box (f (a, b, c))
    static member Curried3<'A, 'B, 'C, 'R>(f: FSharpFunc<'A, FSharpFunc<'B, FSharpFunc<'C, 'R>>>, a: 'A, b: 'B, c: 'C) : obj = box (f a b c)
    static member Tupled4<'A, 'B, 'C, 'D, 'R>(f: FSharpFunc<'A * 'B * 'C * 'D, 'R>, a: 'A, b: 'B, c: 'C, d: 'D) : obj = box (f (a, b, c, d))
    static member Curried4<'A, 'B, 'C, 'D, 'R>(f: FSharpFunc<'A, FSharpFunc<'B, FSharpFunc<'C, FSharpFunc<'D, 'R>>>>, a: 'A, b: 'B, c: 'C, d: 'D) : obj = box (f a b c d)

/// The `FSharpFunc` types a call with argument types `args` and result `R` can apply, each with
/// the Apply method that does it: the tupled shape and, for two or more arguments, the curried one.
module internal FunctionShapes =
    let private funcType (d: Type) (r: Type) = typedefof<FSharpFunc<_, _>>.MakeGenericType(d, r)

    let candidates (args: Type list) (result: Type) : (Type * MethodInfo) list =
        let apply name (types: Type list) = typeof<Apply>.GetMethod(name).MakeGenericMethod(Array.ofList types)
        match args with
        | [] -> [ funcType typeof<unit> result, apply "Unit" [ result ] ]
        | [ a ] -> [ funcType a result, apply "One" [ a; result ] ]
        | _ ->
            let n = args.Length
            [ funcType (FSharp.Reflection.FSharpType.MakeTupleType(Array.ofList args)) result, apply (sprintf "Tupled%d" n) (args @ [ result ])
              List.foldBack funcType args result, apply (sprintf "Curried%d" n) (args @ [ result ]) ]

    let private typeRestrictions (value: DynamicMetaObject) (valueType: Type) (args: DynamicMetaObject[]) =
        Array.fold (fun (r: BindingRestrictions) (a: DynamicMetaObject) -> r.Merge(BindingRestrictions.GetTypeRestriction(a.Expression, a.LimitType)))
            (BindingRestrictions.GetTypeRestriction(value.Expression, valueType)) args

    /// A rule applying `read` (an expression whose static type `valueType` is a candidate) with `args`.
    let applyRule (candidates: (Type * MethodInfo) list) (valueType: Type) (read: Expression) (args: DynamicMetaObject[]) : (MethodInfo * Expression) option =
        candidates
        |> List.tryFind (fun (t, _) -> t.IsAssignableFrom valueType)
        |> Option.map (fun (t, apply) ->
            let ps = apply.GetParameters()
            let arguments = (Expression.Convert(read, t) :> Expression) :: [ for i, a in Array.indexed args -> Expression.Convert(a.Expression, ps.[i + 1].ParameterType) :> Expression ]
            apply, (Expression.Call(apply, arguments) :> Expression))

    /// A rule applying `value` with `args` if its runtime type is a candidate: the DLR caches it
    /// under that type restriction, so a site keeps one rule per kind of value it sees.
    let tryApply (candidates: (Type * MethodInfo) list) (value: DynamicMetaObject) (args: DynamicMetaObject[]) : DynamicMetaObject option =
        match value.RuntimeType with
        | null -> None
        | runtime ->
            applyRule candidates runtime value.Expression args
            |> Option.map (fun (_, call) -> DynamicMetaObject(call, typeRestrictions value runtime args))

/// C#'s Invoke binder, aware of F# function targets (`Dlr.call` on a function value).
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpInvokeBinder(csharp: InvokeBinder, candidates: (Type * MethodInfo) list) =
    inherit InvokeBinder(csharp.CallInfo)

    override this.FallbackInvoke(target, args, errorSuggestion) =
        // A dynamic object's member value arrives without a value at bind time: defer, so the
        // nested site binds through this binder once the value is known.
        if not target.HasValue || args |> Array.exists (fun a -> not a.HasValue) then this.Defer(target, args)
        else
            match FunctionShapes.tryApply candidates target args with
            | Some rule -> rule
            | None -> csharp.FallbackInvoke(target, args, errorSuggestion)

/// F# optional parameters (`?arg`) compile to `FSharpOption<'T>` parameters carrying
/// `[<OptionalArgument>]` and nothing the C# binder recognises, so it can neither omit them nor,
/// on its own, tell that a bare value should become `Some`. This finds a method the supplied
/// arguments fit once omitted optionals are `None` and bare values are wrapped, as a rule the
/// binder offers C# as its error suggestion: used only where C# itself could not bind.
module internal OptionalArguments =
    let private isOptional (p: ParameterInfo) =
        p.GetCustomAttributes(typeof<OptionalArgumentAttribute>, false).Length > 0
        && p.ParameterType.IsGenericType
        && p.ParameterType.GetGenericTypeDefinition() = typedefof<option<_>>

    /// The argument converted to the parameter type, or None if it does not fit.
    let private fit (p: ParameterInfo) (a: DynamicMetaObject) : Expression option =
        let pt = p.ParameterType
        let at = a.LimitType
        if pt.IsAssignableFrom at then Some(Expression.Convert(a.Expression, pt) :> Expression)
        elif isOptional p && pt.GetGenericArguments().[0].IsAssignableFrom at then
            let inner = pt.GetGenericArguments().[0]
            Some(Expression.Call(pt.GetMethod("Some"), Expression.Convert(a.Expression, inner)) :> Expression)
        else None

    let tryCall (t: Type) (name: string) (target: DynamicMetaObject) (args: DynamicMetaObject[]) : DynamicMetaObject option =
        t.GetMethods(BindingFlags.Public ||| BindingFlags.Instance)
        |> Array.filter (fun m -> m.Name = name && not m.IsGenericMethodDefinition)
        |> Array.sortBy (fun m -> m.GetParameters().Length)
        |> Array.tryPick (fun m ->
            let ps = m.GetParameters()
            let required = ps |> Array.filter (fun p -> not (isOptional p)) |> Array.length
            if args.Length < required || args.Length > ps.Length then None
            else
                let supplied = [ for i in 0 .. args.Length - 1 -> fit ps.[i] args.[i] ]
                if supplied |> List.exists Option.isNone then None
                else
                    let omitted = [ for i in args.Length .. ps.Length - 1 -> Expression.Constant(null, ps.[i].ParameterType) :> Expression ]
                    let call = Expression.Call(Expression.Convert(target.Expression, t), m, List.choose id supplied @ omitted)
                    let value =
                        if m.ReturnType = typeof<Void> then Expression.Block(call, Expression.Constant(null, typeof<obj>)) :> Expression
                        else Expression.Convert(call, typeof<obj>) :> Expression
                    let restrictions =
                        Array.fold (fun (r: BindingRestrictions) (a: DynamicMetaObject) -> r.Merge(BindingRestrictions.GetTypeRestriction(a.Expression, a.LimitType)))
                            (BindingRestrictions.GetTypeRestriction(target.Expression, t)) args
                    Some(DynamicMetaObject(value, restrictions)))

/// C#'s InvokeMember binder, aware of F# function values: when C# cannot invoke a member because
/// it holds an `FSharpFunc` rather than a delegate, the rule applies the function instead. The
/// decision is a binding rule restricted to the runtime type, so a site that sees several kinds of
/// target keeps one cached rule per kind, as the DLR intends: no exceptions, no per-site state.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpInvokeMemberBinder(name: string, csharp: InvokeMemberBinder, csharpInvoke: InvokeBinder, candidates: (Type * MethodInfo) list) =
    inherit InvokeMemberBinder(name, false, csharp.CallInfo)
    let invoke = FSharpInvokeBinder(csharpInvoke, candidates)

    /// A CLR target: if it has a public property or field of that name whose type is a candidate,
    /// apply it; otherwise C#'s own binding.
    override _.FallbackInvokeMember(target, args, errorSuggestion) =
        let t = target.LimitType
        let read =
            match t.GetProperty(name, BindingFlags.Public ||| BindingFlags.Instance) with
            | null ->
                match t.GetField(name, BindingFlags.Public ||| BindingFlags.Instance) with
                | null -> None
                | f -> Some(f.FieldType, Expression.Field(Expression.Convert(target.Expression, t), f) :> Expression)
            | p -> Some(p.PropertyType, Expression.Property(Expression.Convert(target.Expression, t), p) :> Expression)
        match read |> Option.bind (fun (mt, r) -> FunctionShapes.applyRule candidates mt r args) with
        | Some(_, call) ->
            let restrictions =
                Array.fold (fun (r: BindingRestrictions) (a: DynamicMetaObject) -> r.Merge(BindingRestrictions.GetTypeRestriction(a.Expression, a.LimitType)))
                    (BindingRestrictions.GetTypeRestriction(target.Expression, t)) args
            DynamicMetaObject(call, restrictions)
        | None ->
            // A method with F# optional parameters, as the rule C# falls back to if it cannot bind.
            let suggestion =
                if target.HasValue && (args |> Array.forall (fun a -> a.HasValue)) then
                    match OptionalArguments.tryCall t name target args with
                    | Some rule -> rule
                    | None -> errorSuggestion
                else errorSuggestion
            csharp.FallbackInvokeMember(target, args, suggestion)

    /// A dynamic target (Expando, DynamicObject) produced the member's value and asks for it to be
    /// invoked: an F# function is applied, anything else is C#'s Invoke (see FSharpInvokeBinder).
    override _.FallbackInvoke(target, args, errorSuggestion) = invoke.FallbackInvoke(target, args, errorSuggestion)

/// The value of a member read as `unit -> R`: an F# function is applied, a delegate invoked, any
/// other value is the result itself.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpReadOrInvokeValueBinder(csharpInvoke: InvokeBinder, candidates: (Type * MethodInfo) list) =
    inherit InvokeBinder(csharpInvoke.CallInfo)

    override this.FallbackInvoke(target, args, errorSuggestion) =
        if not target.HasValue then this.Defer(target, args)
        else
            match FunctionShapes.tryApply candidates target args with
            | Some rule -> rule
            | None ->
                match target.RuntimeType with
                | rt when not (isNull rt) && typeof<Delegate>.IsAssignableFrom rt -> csharpInvoke.FallbackInvoke(target, args, errorSuggestion)
                | rt when not (isNull rt) -> DynamicMetaObject(Expression.Convert(target.Expression, typeof<obj>), BindingRestrictions.GetTypeRestriction(target.Expression, rt))
                | _ -> DynamicMetaObject(Expression.Convert(target.Expression, typeof<obj>), BindingRestrictions.GetInstanceRestriction(target.Expression, null))

/// A member read as `unit -> R`: a parameterless method is invoked, a property or field is read,
/// an F# function value is applied. Decided per runtime type at bind time, like the binders above.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpReadOrInvokeBinder(name: string, csharp: InvokeMemberBinder, csharpInvoke: InvokeBinder, candidates: (Type * MethodInfo) list) =
    inherit InvokeMemberBinder(name, false, csharp.CallInfo)
    let value = FSharpReadOrInvokeValueBinder(csharpInvoke, candidates)

    override _.FallbackInvokeMember(target, args, errorSuggestion) =
        let t = target.LimitType
        let read =
            match t.GetProperty(name, BindingFlags.Public ||| BindingFlags.Instance) with
            | null ->
                match t.GetField(name, BindingFlags.Public ||| BindingFlags.Instance) with
                | null -> None
                | f -> Some(f.FieldType, Expression.Field(Expression.Convert(target.Expression, t), f) :> Expression)
            | p when not (isNull (p.GetGetMethod())) && p.GetIndexParameters().Length = 0 -> Some(p.PropertyType, Expression.Property(Expression.Convert(target.Expression, t), p) :> Expression)
            | _ -> None
        match read with
        | None -> csharp.FallbackInvokeMember(target, args, errorSuggestion)
        | Some(mt, r) ->
            let value =
                match FunctionShapes.applyRule candidates mt r args with
                | Some(_, call) -> call
                | None -> Expression.Convert(r, typeof<obj>) :> Expression
            DynamicMetaObject(value, BindingRestrictions.GetTypeRestriction(target.Expression, t))

    override _.FallbackInvoke(target, args, errorSuggestion) = value.FallbackInvoke(target, args, errorSuggestion)

/// A member read as an F# function type: `let f: int -> int -> int = dlr { return x?Add }`. The
/// value is an F# function (curried, so partial application works) that invokes the member with
/// the collected arguments when fully applied and converts the result; the site's binder handles
/// methods, delegates and F# function values. Tupled variants for `A * B -> R`.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
[<AbstractClass; Sealed>]
type FunctionMember =
    static member Curried0<'R>(invoke: CallSite<Func<CallSite, obj, obj>>, convert: CallSite<Func<CallSite, obj, 'R>>, target: obj) : unit -> 'R =
        fun () -> convert.Target.Invoke(convert, invoke.Target.Invoke(invoke, target))
    static member Curried1<'A, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, obj>>, convert: CallSite<Func<CallSite, obj, 'R>>, target: obj) : 'A -> 'R =
        fun a -> convert.Target.Invoke(convert, invoke.Target.Invoke(invoke, target, a))
    static member Curried2<'A, 'B, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, obj>>, convert: CallSite<Func<CallSite, obj, 'R>>, target: obj) : 'A -> 'B -> 'R =
        fun a b -> convert.Target.Invoke(convert, invoke.Target.Invoke(invoke, target, a, b))
    static member Curried3<'A, 'B, 'C, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, 'C, obj>>, convert: CallSite<Func<CallSite, obj, 'R>>, target: obj) : 'A -> 'B -> 'C -> 'R =
        fun a b c -> convert.Target.Invoke(convert, invoke.Target.Invoke(invoke, target, a, b, c))
    static member Curried4<'A, 'B, 'C, 'D, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, 'C, 'D, obj>>, convert: CallSite<Func<CallSite, obj, 'R>>, target: obj) : 'A -> 'B -> 'C -> 'D -> 'R =
        fun a b c d -> convert.Target.Invoke(convert, invoke.Target.Invoke(invoke, target, a, b, c, d))
    static member Tupled2<'A, 'B, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, obj>>, convert: CallSite<Func<CallSite, obj, 'R>>, target: obj) : 'A * 'B -> 'R =
        fun (a, b) -> convert.Target.Invoke(convert, invoke.Target.Invoke(invoke, target, a, b))
    static member Tupled3<'A, 'B, 'C, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, 'C, obj>>, convert: CallSite<Func<CallSite, obj, 'R>>, target: obj) : 'A * 'B * 'C -> 'R =
        fun (a, b, c) -> convert.Target.Invoke(convert, invoke.Target.Invoke(invoke, target, a, b, c))
    static member Tupled4<'A, 'B, 'C, 'D, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, 'C, 'D, obj>>, convert: CallSite<Func<CallSite, obj, 'R>>, target: obj) : 'A * 'B * 'C * 'D -> 'R =
        fun (a, b, c, d) -> convert.Target.Invoke(convert, invoke.Target.Invoke(invoke, target, a, b, c, d))

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

    /// C#'s InvokeMember binder wrapped to apply F# function values (see FSharpInvokeMemberBinder)
    /// for positional, non-generic calls of up to four arguments; otherwise C#'s binder as is.
    let private smartInvokeMember (context: Type) (name: string) (typeArgs: Type list) (discard: bool) (resultType: Type) (all: Arg list) : CallSiteBinder =
        let args = List.tail all
        let flags = if discard then CSharpBinderFlags.ResultDiscarded else CSharpBinderFlags.None
        let typeArgSeq = match typeArgs with [] -> null | ts -> ts :> seq<Type>
        let csharp = Binder.InvokeMember(flags, name, typeArgSeq, context, [ for a in all -> argInfo a ])
        let positional = args |> List.forall (fun a -> isNull a.Name)
        if not positional || not typeArgs.IsEmpty || args.Length > 4 then csharp
        else
            // Discarded results too: the site is void-returning and the DLR drops the rule's value.
            let csharpInvoke = Binder.Invoke(flags, context, [ for a in all -> argInfo a ]) :?> InvokeBinder
            let result = if discard then typeof<unit> else resultType
            FSharpInvokeMemberBinder(name, csharp :?> InvokeMemberBinder, csharpInvoke, FunctionShapes.candidates [ for a in args -> a.Type ] result) :> CallSiteBinder

    /// `x?Name(args)` whose inferred type is `A -> R`: InvokeMember, applying an F# function value
    /// held by the member when C# cannot invoke it.
    let invokeMemberOrApply (context: Type) (name: string) (typeArgs: Type list) (discard: bool) (resultType: Type) (target: Arg) (args: Arg list) =
        let all = target :: args
        siteCall (smartInvokeMember context name typeArgs discard resultType all) all (if discard then voidType else typeof<obj>)

    /// `Dlr.call args target`, applying `target` itself when it is an F# function.
    let invokeOrApply (context: Type) (discard: bool) (resultType: Type) (target: Arg) (args: Arg list) =
        let all = target :: args
        let csharp = Binder.Invoke((if discard then CSharpBinderFlags.ResultDiscarded else CSharpBinderFlags.None), context, [ for a in all -> argInfo a ])
        let positional = args |> List.forall (fun a -> isNull a.Name)
        let result = if discard then typeof<unit> else resultType
        let binder =
            if not positional || args.Length > 4 then csharp
            else FSharpInvokeBinder(csharp :?> InvokeBinder, FunctionShapes.candidates [ for a in args -> a.Type ] result) :> CallSiteBinder
        siteCall binder all (if discard then voidType else typeof<obj>)

    /// `x?Name` read as an F# function type (see FunctionMember): the argument types come from the
    /// function type's domains, curried or tupled; `unit -> R` reads a property or invokes.
    let functionMember (context: Type) (name: string) (functionType: Type) (target: Arg) : Expr =
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
        let invokeArgs = [ for t in argTypes -> typedArg (Expr.Value(null, t)) ]
        let all = target :: invokeArgs
        let binder =
            if argTypes.IsEmpty then
                let csharp = Binder.InvokeMember(CSharpBinderFlags.None, name, null, context, [ argInfo target ]) :?> InvokeMemberBinder
                let csharpInvoke = Binder.Invoke(CSharpBinderFlags.None, context, [ argInfo target ]) :?> InvokeBinder
                FSharpReadOrInvokeBinder(name, csharp, csharpInvoke, FunctionShapes.candidates [] resultType) :> CallSiteBinder
            else smartInvokeMember context name [] false resultType all
        let invokeSite = site binder all typeof<obj>
        let convertSite = site (Binder.Convert(CSharpBinderFlags.None, resultType, context)) [ dynamicArg (Expr.Value(null, typeof<obj>)) ] resultType
        let helperName = (if tupled then "Tupled" else "Curried") + string argTypes.Length
        let helper = typeof<FunctionMember>.GetMethod(helperName).MakeGenericMethod(Array.ofList (argTypes @ [ resultType ]))
        Expr.Call(helper, [ invokeSite; convertSite; target.Expr ])

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
