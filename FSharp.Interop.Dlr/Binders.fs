namespace FSharp.Interop.Dlr

open System
open System.Dynamic
open System.Linq.Expressions
open System.Reflection
open System.Runtime.CompilerServices
open Microsoft.CSharp.RuntimeBinder
open FSharp.Quotations

/// Raised when a `dlr { }` body uses something the translator does not handle.
type DlrTranslationException(message: string) =
    inherit Exception(message)

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

/// The accessibility rule the C# binder applies from its context type: public members always;
/// internal ones (which is what F# `private` compiles to) from the same assembly; private ones
/// from inside the declaring type. Our own reflection lookups apply the same rule.
module internal Accessibility =
    let private sameAssembly (context: Type) (declaring: Type) = context.Assembly = declaring.Assembly
    let rec private within (context: Type) (declaring: Type) =
        not (isNull context) && (context = declaring || (context.IsNested && within context.DeclaringType declaring))
    let rec private derived (context: Type) (declaring: Type) =
        not (isNull context) && (declaring.IsAssignableFrom context || (context.IsNested && derived context.DeclaringType declaring))

    /// The C# rule: public; internal from the assembly; protected from a derived type; protected
    /// internal from either; private protected from a derived type in the assembly; private from
    /// inside the declaring type (including nested types).
    let private accessible (context: Type) (declaring: Type) (isPublic, isAssembly, isFamily, isFamilyOrAssembly, isFamilyAndAssembly, isPrivate) =
        isPublic
        || (isAssembly && sameAssembly context declaring)
        || (isFamily && derived context declaring)
        || (isFamilyOrAssembly && (sameAssembly context declaring || derived context declaring))
        || (isFamilyAndAssembly && sameAssembly context declaring && derived context declaring)
        || (isPrivate && within context declaring)

    let method' (context: Type) (m: MethodBase) =
        accessible context m.DeclaringType (m.IsPublic, m.IsAssembly, m.IsFamily, m.IsFamilyOrAssembly, m.IsFamilyAndAssembly, m.IsPrivate)

    let field (context: Type) (f: FieldInfo) =
        accessible context f.DeclaringType (f.IsPublic, f.IsAssembly, f.IsFamily, f.IsFamilyOrAssembly, f.IsFamilyAndAssembly, f.IsPrivate)

    let all = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance

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

    /// Not C#'s overload resolution, but deterministic: among the methods the arguments fit, the
    /// one with the most exactly-typed argument slots wins, then the one with the fewest omitted
    /// parameters; a tie is ambiguous and left to C#'s error.
    let tryCall (context: Type) (t: Type) (name: string) (target: DynamicMetaObject) (args: DynamicMetaObject[]) : DynamicMetaObject option =
        let fitting =
            t.GetMethods(Accessibility.all)
            |> Array.filter (fun m -> m.Name = name && not m.IsGenericMethodDefinition && Accessibility.method' context m)
            |> Array.choose (fun m ->
                let ps = m.GetParameters()
                let required = ps |> Array.filter (fun p -> not (isOptional p)) |> Array.length
                if args.Length < required || args.Length > ps.Length then None
                else
                    let supplied = [ for i in 0 .. args.Length - 1 -> fit ps.[i] args.[i] ]
                    if supplied |> List.exists Option.isNone then None
                    else
                        let exact = Seq.zip ps args |> Seq.filter (fun (p, a) -> p.ParameterType = a.LimitType) |> Seq.length
                        Some(m, ps, List.choose id supplied, exact))
            |> Array.sortByDescending (fun (_, ps, _, exact) -> exact, -ps.Length)
        match List.ofArray fitting with
        | (_, ps1, _, e1) :: (_, ps2, _, e2) :: _ when e1 = e2 && ps1.Length = ps2.Length -> None   // ambiguous
        | (m, ps, supplied, _) :: _ ->
            let omitted = [ for i in args.Length .. ps.Length - 1 -> Expression.Constant(null, ps.[i].ParameterType) :> Expression ]
            let call = Expression.Call(Expression.Convert(target.Expression, t), m, supplied @ omitted)
            let value =
                if m.ReturnType = typeof<Void> then Expression.Block(call, Expression.Constant(null, typeof<obj>)) :> Expression
                else Expression.Convert(call, typeof<obj>) :> Expression
            let restrictions =
                Array.fold (fun (r: BindingRestrictions) (a: DynamicMetaObject) -> r.Merge(BindingRestrictions.GetTypeRestriction(a.Expression, a.LimitType)))
                    (BindingRestrictions.GetTypeRestriction(target.Expression, t)) args
            Some(DynamicMetaObject(value, restrictions))
        | [] -> None

/// Applying an F# function value with the arguments of a call site: one overload per shape,
/// tupled or curried. Each returns the result boxed; the site's Convert does the rest.
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

/// Whether a function type (the runtime type of a value, or a member's declared type) is an
/// `FSharpFunc` that the call site's arguments fit, and how to apply it. The shapes come from the
/// function itself, not from the call's declared result: a discarded result or a widened one
/// still applies the function that is actually there.
module internal FunctionShapes =
    let private isFunc (t: Type) = t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<FSharpFunc<_, _>>

    /// C#'s implicit numeric conversions, so an `int` argument fits an `int64` or `float` domain.
    let private widens (from: Type) (``to``: Type) =
        let n = [ typeof<sbyte>, [ typeof<int16>; typeof<int32>; typeof<int64>; typeof<single>; typeof<double>; typeof<decimal> ]
                  typeof<byte>, [ typeof<int16>; typeof<uint16>; typeof<int32>; typeof<uint32>; typeof<int64>; typeof<uint64>; typeof<single>; typeof<double>; typeof<decimal> ]
                  typeof<int16>, [ typeof<int32>; typeof<int64>; typeof<single>; typeof<double>; typeof<decimal> ]
                  typeof<uint16>, [ typeof<int32>; typeof<uint32>; typeof<int64>; typeof<uint64>; typeof<single>; typeof<double>; typeof<decimal> ]
                  typeof<int32>, [ typeof<int64>; typeof<single>; typeof<double>; typeof<decimal> ]
                  typeof<uint32>, [ typeof<int64>; typeof<uint64>; typeof<single>; typeof<double>; typeof<decimal> ]
                  typeof<int64>, [ typeof<single>; typeof<double>; typeof<decimal> ]
                  typeof<uint64>, [ typeof<single>; typeof<double>; typeof<decimal> ]
                  typeof<char>, [ typeof<uint16>; typeof<int32>; typeof<uint32>; typeof<int64>; typeof<uint64>; typeof<single>; typeof<double>; typeof<decimal> ]
                  typeof<single>, [ typeof<double> ] ]
        n |> List.exists (fun (f, ts) -> f = from && List.contains ``to`` ts)

    /// Assignable, or reachable by an implicit numeric conversion (`Expression.Convert` does
    /// the widening in the rule).
    let private fits (paramType: Type) (argType: Type) = paramType.IsAssignableFrom argType || widens argType paramType

    /// The `FSharpFunc<_, _>` a type is or derives from: a function value's runtime type is a
    /// compiler-generated subclass (`f@12`), a member's declared type usually the base itself.
    let rec private funcBase (t: Type) : Type option =
        if isNull t then None
        elif isFunc t then Some t
        else funcBase t.BaseType

    /// The curried domains and final result of an FSharpFunc type, up to `n` domains.
    let rec private curried (n: int) (t: Type) : (Type list * Type) option =
        if n = 0 then Some([], t)
        elif isFunc t then
            let ga = t.GetGenericArguments()
            curried (n - 1) ga.[1] |> Option.map (fun (ds, r) -> ga.[0] :: ds, r)
        else None

    /// The Apply method for `funcType` applied to arguments of `argTypes`, if it fits.
    let applyFor (argTypes: Type list) (funcType: Type) : MethodInfo option =
        match funcBase funcType with
        | None -> None
        | Some funcType ->
            let ga = funcType.GetGenericArguments()
            let domain, result = ga.[0], ga.[1]
            let apply name (types: Type list) = typeof<Apply>.GetMethod(name).MakeGenericMethod(Array.ofList types)
            match argTypes with
            | [] when domain = typeof<unit> -> Some(apply "Unit" [ result ])
            | [ a ] when fits domain a -> Some(apply "One" [ domain; result ])
            | [] | [ _ ] -> None
            | _ ->
                let n = argTypes.Length
                if FSharp.Reflection.FSharpType.IsTuple domain
                   && (let es = FSharp.Reflection.FSharpType.GetTupleElements domain in es.Length = n && List.forall2 fits (List.ofArray es) argTypes) then
                    Some(apply (sprintf "Tupled%d" n) (List.ofArray (FSharp.Reflection.FSharpType.GetTupleElements domain) @ [ result ]))
                else
                    match curried n funcType with
                    | Some(ds, r) when List.forall2 fits ds argTypes -> Some(apply (sprintf "Curried%d" n) (ds @ [ r ]))
                    | _ -> None

    let private restrictions (target: DynamicMetaObject) (targetType: Type) (args: DynamicMetaObject[]) =
        Array.fold (fun (r: BindingRestrictions) (a: DynamicMetaObject) -> r.Merge(BindingRestrictions.GetTypeRestriction(a.Expression, a.LimitType)))
            (BindingRestrictions.GetTypeRestriction(target.Expression, targetType)) args

    /// The argument types a shape has to fit: each meta-object's LimitType, i.e. the runtime type
    /// of a dynamic (`obj`-typed) argument and the static type of a typed one.
    let argTypes (args: DynamicMetaObject[]) = [ for a in args -> a.LimitType ]

    /// The call applying `read` (an expression of `funcType`) with `args`, if the shape fits.
    let applyCall (funcType: Type) (read: Expression) (args: DynamicMetaObject[]) : Expression option =
        applyFor (argTypes args) funcType
        |> Option.map (fun apply ->
            let ps = apply.GetParameters()
            let arguments = (Expression.Convert(read, ps.[0].ParameterType) :> Expression) :: [ for i, a in Array.indexed args -> Expression.Convert(a.Expression, ps.[i + 1].ParameterType) :> Expression ]
            Expression.Call(apply, arguments) :> Expression)

    /// A rule applying `value` with `args` if its runtime type is a fitting FSharpFunc: the DLR
    /// caches it under that type restriction, so a site keeps one rule per kind of value it sees.
    let tryApply (value: DynamicMetaObject) (args: DynamicMetaObject[]) : DynamicMetaObject option =
        match value.RuntimeType with
        | null -> None
        | runtime ->
            applyCall runtime value.Expression args
            |> Option.map (fun call -> DynamicMetaObject(call, restrictions value runtime args))

    /// An instance property (non-indexed) or field of `t` named `name` that `context` may access:
    /// its type and a read.
    let clrMember (context: Type) (t: Type) (name: string) (target: DynamicMetaObject) : (Type * Expression) option =
        let self = Expression.Convert(target.Expression, t)
        let property =
            t.GetProperties(Accessibility.all)
            |> Array.tryFind (fun p ->
                p.Name = name && p.GetIndexParameters().Length = 0
                && (let g = p.GetGetMethod(true) in not (isNull g) && Accessibility.method' context g))
        match property with
        | Some p -> Some(p.PropertyType, Expression.Property(self, p) :> Expression)
        | None ->
            match t.GetFields(Accessibility.all) |> Array.tryFind (fun f -> f.Name = name && Accessibility.field context f) with
            | None -> None
            | Some f -> Some(f.FieldType, Expression.Field(self, f) :> Expression)

    /// Whether a member's declared type says nothing useful about whether it holds a function:
    /// `obj`, an interface, an abstract class. Its value then goes through a nested site.
    let opaque (t: Type) = t = typeof<obj> || t.IsInterface || (t.IsAbstract && not t.IsSealed)

/// C#'s Invoke binder, aware of F# function targets (`Dlr.call` on a function value, and the
/// value step of a member invocation).
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpInvokeBinder(csharp: InvokeBinder) =
    inherit InvokeBinder(csharp.CallInfo)

    override this.FallbackInvoke(target, args, errorSuggestion) =
        // A dynamic object's member value arrives without a value at bind time: defer, so the
        // nested site binds through this binder once the value is known.
        if not target.HasValue || args |> Array.exists (fun a -> not a.HasValue) then this.Defer(target, args)
        else
            match FunctionShapes.tryApply target args with
            | Some rule -> rule
            | None -> csharp.FallbackInvoke(target, args, errorSuggestion)

/// C#'s InvokeMember binder, aware of F# function values: when C# cannot invoke a member because
/// it holds an `FSharpFunc` rather than a delegate, the rule applies the function instead. The
/// decision is a binding rule restricted to the runtime type, so a site that sees several kinds of
/// target keeps one cached rule per kind, as the DLR intends: no exceptions, no per-site state.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpInvokeMemberBinder(context: Type, name: string, csharp: InvokeMemberBinder, csharpInvoke: InvokeBinder) =
    inherit InvokeMemberBinder(name, false, csharp.CallInfo)
    let invoke = FSharpInvokeBinder(csharpInvoke)

    /// A CLR target: a public property or field of that name whose declared type is a fitting
    /// FSharpFunc is applied directly; one whose declared type says nothing (`obj`, an interface)
    /// is read and handed to a nested Invoke site that decides by the value's runtime type;
    /// otherwise C#'s own binding, with a rule for F# optional parameters as its error suggestion.
    override _.FallbackInvokeMember(target, args, errorSuggestion) =
        let t = target.LimitType
        let restrictions =
            Array.fold (fun (r: BindingRestrictions) (a: DynamicMetaObject) -> r.Merge(BindingRestrictions.GetTypeRestriction(a.Expression, a.LimitType)))
                (BindingRestrictions.GetTypeRestriction(target.Expression, t)) args
        let direct =
            FunctionShapes.clrMember context t name target
            |> Option.bind (fun (mt, read) ->
                match FunctionShapes.applyCall mt read args with
                | Some call -> Some(DynamicMetaObject(call, restrictions))
                | None when FunctionShapes.opaque mt ->
                    let nested = Expression.Dynamic(invoke, typeof<obj>, (read :: [ for a in args -> a.Expression ]))
                    Some(DynamicMetaObject(nested, restrictions))
                | None -> None)
        let hasMethod =
            t.GetMethods(Accessibility.all) |> Array.exists (fun m -> m.Name = name && Accessibility.method' context m)
        match direct with
        | Some rule when not hasMethod -> rule
        | _ ->
            // A method of that name exists: C# binds it; our rules (a function-valued member of
            // the same name, or F# optional parameters) are only its error suggestion.
            let suggestion =
                match direct with
                | Some rule -> rule
                | None ->
                    if target.HasValue && (args |> Array.forall (fun a -> a.HasValue)) then
                        match OptionalArguments.tryCall context t name target args with
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
type FSharpReadOrInvokeValueBinder(csharpInvoke: InvokeBinder) =
    inherit InvokeBinder(csharpInvoke.CallInfo)

    override this.FallbackInvoke(target, args, errorSuggestion) =
        if not target.HasValue then this.Defer(target, args)
        else
            match FunctionShapes.tryApply target args with
            | Some rule -> rule
            | None ->
                match target.RuntimeType with
                | rt when not (isNull rt) && typeof<Delegate>.IsAssignableFrom rt -> csharpInvoke.FallbackInvoke(target, args, errorSuggestion)
                | rt when not (isNull rt) -> DynamicMetaObject(Expression.Convert(target.Expression, typeof<obj>), BindingRestrictions.GetTypeRestriction(target.Expression, rt))
                | _ -> DynamicMetaObject(Expression.Convert(target.Expression, typeof<obj>), BindingRestrictions.GetInstanceRestriction(target.Expression, null))

/// A member read as `unit -> R`: a parameterless method is invoked; a property or field is read,
/// then applied or invoked if it holds a function or delegate (decided by declared type when it
/// says so, by runtime type through a nested site otherwise), else its value is the result.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpReadOrInvokeBinder(context: Type, name: string, csharp: InvokeMemberBinder, csharpInvoke: InvokeBinder) =
    inherit InvokeMemberBinder(name, false, csharp.CallInfo)
    let value = FSharpReadOrInvokeValueBinder(csharpInvoke)

    override _.FallbackInvokeMember(target, args, errorSuggestion) =
        let t = target.LimitType
        match FunctionShapes.clrMember context t name target with
        | None -> csharp.FallbackInvokeMember(target, args, errorSuggestion)
        | Some(mt, read) ->
            let restriction = BindingRestrictions.GetTypeRestriction(target.Expression, t)
            match FunctionShapes.applyCall mt read args with
            | Some call -> DynamicMetaObject(call, restriction)
            | None when mt.IsValueType || mt = typeof<string> -> DynamicMetaObject(Expression.Convert(read, typeof<obj>), restriction)
            | None -> DynamicMetaObject(Expression.Dynamic(value, typeof<obj>, read), restriction)

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
    // `… -> unit`: a void site, nothing to convert.
    static member Curried0Unit(invoke: CallSite<Action<CallSite, obj>>, target: obj) : unit -> unit =
        fun () -> invoke.Target.Invoke(invoke, target)
    static member Curried1Unit<'A>(invoke: CallSite<Action<CallSite, obj, 'A>>, target: obj) : 'A -> unit =
        fun a -> invoke.Target.Invoke(invoke, target, a)
    static member Curried2Unit<'A, 'B>(invoke: CallSite<Action<CallSite, obj, 'A, 'B>>, target: obj) : 'A -> 'B -> unit =
        fun a b -> invoke.Target.Invoke(invoke, target, a, b)
    static member Curried3Unit<'A, 'B, 'C>(invoke: CallSite<Action<CallSite, obj, 'A, 'B, 'C>>, target: obj) : 'A -> 'B -> 'C -> unit =
        fun a b c -> invoke.Target.Invoke(invoke, target, a, b, c)
    static member Curried4Unit<'A, 'B, 'C, 'D>(invoke: CallSite<Action<CallSite, obj, 'A, 'B, 'C, 'D>>, target: obj) : 'A -> 'B -> 'C -> 'D -> unit =
        fun a b c d -> invoke.Target.Invoke(invoke, target, a, b, c, d)
    static member Tupled2Unit<'A, 'B>(invoke: CallSite<Action<CallSite, obj, 'A, 'B>>, target: obj) : 'A * 'B -> unit =
        fun (a, b) -> invoke.Target.Invoke(invoke, target, a, b)
    static member Tupled3Unit<'A, 'B, 'C>(invoke: CallSite<Action<CallSite, obj, 'A, 'B, 'C>>, target: obj) : 'A * 'B * 'C -> unit =
        fun (a, b, c) -> invoke.Target.Invoke(invoke, target, a, b, c)
    static member Tupled4Unit<'A, 'B, 'C, 'D>(invoke: CallSite<Action<CallSite, obj, 'A, 'B, 'C, 'D>>, target: obj) : 'A * 'B * 'C * 'D -> unit =
        fun (a, b, c, d) -> invoke.Target.Invoke(invoke, target, a, b, c, d)

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
    let private smartInvokeMember (context: Type) (name: string) (typeArgs: Type list) (discard: bool) (all: Arg list) : CallSiteBinder =
        let args = List.tail all
        let flags = if discard then CSharpBinderFlags.ResultDiscarded else CSharpBinderFlags.None
        let typeArgSeq = match typeArgs with [] -> null | ts -> ts :> seq<Type>
        let csharp = Binder.InvokeMember(flags, name, typeArgSeq, context, [ for a in all -> argInfo a ])
        let positional = args |> List.forall (fun a -> isNull a.Name)
        if not positional || not typeArgs.IsEmpty || args.Length > 4 then csharp
        else
            // Discarded results too: the site is void-returning and the DLR drops the rule's value.
            let csharpInvoke = Binder.Invoke(flags, context, [ for a in all -> argInfo a ]) :?> InvokeBinder
            FSharpInvokeMemberBinder(context, name, csharp :?> InvokeMemberBinder, csharpInvoke) :> CallSiteBinder

    /// `x?Name(args)` whose inferred type is `A -> R`: InvokeMember, applying an F# function value
    /// held by the member when C# cannot invoke it.
    let invokeMemberOrApply (context: Type) (name: string) (typeArgs: Type list) (discard: bool) (target: Arg) (args: Arg list) =
        let all = target :: args
        siteCall (smartInvokeMember context name typeArgs discard all) all (if discard then voidType else typeof<obj>)

    /// `Dlr.call args target`, applying `target` itself when it is an F# function.
    let invokeOrApply (context: Type) (discard: bool) (target: Arg) (args: Arg list) =
        let all = target :: args
        let csharp = Binder.Invoke((if discard then CSharpBinderFlags.ResultDiscarded else CSharpBinderFlags.None), context, [ for a in all -> argInfo a ])
        let positional = args |> List.forall (fun a -> isNull a.Name)
        let binder =
            if not positional || args.Length > 4 then csharp
            else FSharpInvokeBinder(csharp :?> InvokeBinder) :> CallSiteBinder
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
        if argTypes.Length > 4 then
            raise (DlrTranslationException(
                    sprintf "dlr { } can read a member as a function of up to four arguments; '%s' is read as one of %d. F# function values are applied with up to four arguments; beyond that store a delegate." name argTypes.Length))
        let invokeArgs = [ for t in argTypes -> typedArg (Expr.Value(null, t)) ]
        let all = target :: invokeArgs
        // `… -> unit` invokes with the result discarded (a void site), as a statement call does.
        let discard = resultType = typeof<unit>
        let flags = if discard then CSharpBinderFlags.ResultDiscarded else CSharpBinderFlags.None
        let binder =
            if argTypes.IsEmpty then
                let csharp = Binder.InvokeMember(flags, name, null, context, [ argInfo target ]) :?> InvokeMemberBinder
                let csharpInvoke = Binder.Invoke(flags, context, [ argInfo target ]) :?> InvokeBinder
                FSharpReadOrInvokeBinder(context, name, csharp, csharpInvoke) :> CallSiteBinder
            else smartInvokeMember context name [] discard all
        let invokeSite = site binder all (if discard then voidType else typeof<obj>)
        let shape = (if tupled then "Tupled" else "Curried") + string argTypes.Length
        if discard then
            let helper = typeof<FunctionMember>.GetMethod(shape + "Unit")
            let helper = if argTypes.IsEmpty then helper else helper.MakeGenericMethod(Array.ofList argTypes)
            Expr.Call(helper, [ invokeSite; target.Expr ])
        else
            let convertSite = site (Binder.Convert(CSharpBinderFlags.None, resultType, context)) [ dynamicArg (Expr.Value(null, typeof<obj>)) ] resultType
            let helper = typeof<FunctionMember>.GetMethod(shape).MakeGenericMethod(Array.ofList (argTypes @ [ resultType ]))
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
