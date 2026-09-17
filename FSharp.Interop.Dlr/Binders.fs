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

/// The implicit conversions C# applies to an argument, so our own rules accept what its binder would.
module internal Conversions =
    /// C#'s implicit numeric conversions, so an `int` argument fits an `int64` or `float` domain.
    let widens (from: Type) (``to``: Type) =
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

    /// Assignable, or a C# implicit numeric widening (`Expression.Convert` does the widening in the rule).
    let fits (paramType: Type) (argType: Type) = paramType.IsAssignableFrom argType || widens argType paramType

/// Whether a function type (the runtime type of a value, or a member's declared type) is an
/// `FSharpFunc` that the call site's arguments fit, and the expression applying it. The shape
/// comes from the function itself, not from the call's declared result: a discarded result or a
/// widened one still applies the function that is there. Any arity: a curried function is a
/// chain of `Invoke` calls, a tupled one a tuple construction and one `Invoke`.
module internal FunctionShapes =
    let private isFunc (t: Type) = t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<FSharpFunc<_, _>>

    let private fits (paramType: Type) (argType: Type) = Conversions.fits paramType argType

    /// The `FSharpFunc<_, _>` a type is or derives from: a function value's runtime type is a
    /// compiler-generated subclass (`f@12`), a member's declared type usually the base itself.
    let rec private funcBase (t: Type) : Type option =
        if isNull t then None
        elif isFunc t then Some t
        else funcBase t.BaseType

    let private invoke (f: Expression) (funcType: Type) (arg: Expression) : Expression =
        Expression.Call(f, funcType.GetMethod("Invoke"), arg) :> Expression

    /// Through the LimitType first: an `obj`-typed argument is unboxed as its runtime type, then
    /// widened; converting `obj` straight to `int64` would unbox a boxed `int` as `int64` and throw.
    let private convertTo (t: Type) (a: DynamicMetaObject) =
        let unboxed = if a.Expression.Type = a.LimitType then a.Expression else Expression.Convert(a.Expression, a.LimitType) :> Expression
        if a.LimitType = t then unboxed else Expression.Convert(unboxed, t) :> Expression

    /// The call applying `read` (an expression whose value is of `funcType`) with `args`, boxed,
    /// if the shape fits: `unit -> R` for no arguments, `A -> R` for one, and for more either a
    /// tuple domain of that size or a curried chain of that depth.
    let applyCall (funcType: Type) (read: Expression) (args: DynamicMetaObject[]) : Expression option =
        match funcBase funcType with
        | None -> None
        | Some ft ->
            let ga = ft.GetGenericArguments()
            let domain = ga.[0]
            let f = Expression.Convert(read, ft) :> Expression
            let boxed (e: Expression) = Expression.Convert(e, typeof<obj>) :> Expression
            match List.ofArray args with
            | [] when domain = typeof<unit> -> Some(boxed (invoke f ft (Expression.Constant(null, typeof<unit>))))
            | [ a ] when fits domain a.LimitType -> Some(boxed (invoke f ft (convertTo domain a)))
            | [] | [ _ ] -> None
            | args ->
                let n = args.Length
                let tupled =
                    if FSharp.Reflection.FSharpType.IsTuple domain then
                        let es = List.ofArray (FSharp.Reflection.FSharpType.GetTupleElements domain)
                        if es.Length = n && List.forall2 fits es (List.map (fun (a: DynamicMetaObject) -> a.LimitType) args) then
                            let tuple = Expression.New(domain.GetConstructor(Array.ofList es), List.map2 convertTo es args)
                            Some(boxed (invoke f ft tuple))
                        else None
                    else None
                match tupled with
                | Some call -> Some call
                | None ->
                    // Curried: each domain in turn must fit, and each step's result is the next function.
                    let rec chain (fe: Expression) (t: Type) (remaining: DynamicMetaObject list) =
                        match remaining with
                        | [] -> Some fe
                        | a :: rest ->
                            match funcBase t with
                            | Some ft when fits (ft.GetGenericArguments().[0]) a.LimitType ->
                                let ga = ft.GetGenericArguments()
                                chain (invoke (Expression.Convert(fe, ft)) ft (convertTo ga.[0] a)) ga.[1] rest
                            | _ -> None
                    chain read funcType args |> Option.map boxed

    /// A function type's parameter list and result: `(A * B) -> R` is `[A; B]` tupled, `A -> B -> R`
    /// is `[A; B]` curried (the chain followed as far as it is `FSharpFunc`), `A -> R` is `[A]`.
    let domains (funcType: Type) : (Type list * bool * Type) option =
        match funcBase funcType with
        | None -> None
        | Some ft ->
            let ga = ft.GetGenericArguments()
            if FSharp.Reflection.FSharpType.IsTuple ga.[0] then
                Some(List.ofArray (FSharp.Reflection.FSharpType.GetTupleElements ga.[0]), true, ga.[1])
            else
                let rec chain (t: Type) (acc: Type list) =
                    if isFunc t then
                        let ga = t.GetGenericArguments()
                        chain ga.[1] (ga.[0] :: acc)
                    else List.rev acc, t
                let ds, r = chain ga.[1] [ ga.[0] ]
                Some(ds, false, r)

    let private restrictions (target: DynamicMetaObject) (targetType: Type) (args: DynamicMetaObject[]) =
        Array.fold (fun (r: BindingRestrictions) (a: DynamicMetaObject) -> r.Merge(BindingRestrictions.GetTypeRestriction(a.Expression, a.LimitType)))
            (BindingRestrictions.GetTypeRestriction(target.Expression, targetType)) args

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

/// One step of a curried F# function built at run time for a member read as a function of more
/// arguments than the FunctionMember helpers cover: each step collects one argument and returns
/// the next step, and the last invokes the site's delegate with all of them. This is exactly what
/// F# emits for a curried function beyond OptimizedClosures' reach, minus InvokeFast.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type CurryStep<'A, 'R>(collected: obj list, next: obj list -> obj) =
    inherit FSharpFunc<'A, 'R>()
    override _.Invoke(a: 'A) : 'R = unbox<'R> (next (collected @ [ box a ]))

[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
module CurriedInvoker =
    /// A curried F# function of the given domain types (nested `FSharpFunc`s) whose final result,
    /// converted, comes from invoking `site` (a `Func<CallSite, obj, args…, obj>`) with all the
    /// arguments; `unitResult` sites are `Action`s.
    /// A curried F# function of the given domain types whose final result is `finish` applied to
    /// all the collected arguments.
    let buildWith (domains: Type list) (resultType: Type) (finish: obj list -> obj) : obj =
        // The function type of the step taking domains.[i]: FSharpFunc<d_i, type of the rest>.
        let rec stepType (ds: Type list) =
            match ds with
            | [] -> resultType
            | d :: rest -> typedefof<FSharpFunc<_, _>>.MakeGenericType(d, stepType rest)
        let rec step (collected: obj list) (ds: Type list) : obj =
            match ds with
            | [] -> finish collected
            | d :: rest ->
                let next (args: obj list) = step args rest
                Activator.CreateInstance(typedefof<CurryStep<_, _>>.MakeGenericType(d, stepType rest), [| box collected; box next |])
        step [] domains

    let build (domains: Type list) (resultType: Type) (site: CallSite) (convert: CallSite) (target: obj) : obj =
        let siteDelegate = site.GetType().GetField("Target").GetValue(site) :?> Delegate
        let convertDelegate = if isNull convert then null else convert.GetType().GetField("Target").GetValue(convert) :?> Delegate
        buildWith domains resultType (fun args ->
            let raw = siteDelegate.DynamicInvoke(Array.ofList (box site :: box target :: args))
            if isNull convertDelegate then null else convertDelegate.DynamicInvoke([| box convert; raw |]))

/// An F# function over a delegate, for a delegate argument passed to a function-typed parameter
/// when `FuncConvert` has no matching shape: tupled (or one parameter) as one closure, curried
/// as a `CurryStep` chain, the delegate invoked with `DynamicInvoke` when all arguments are in.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type TupledDelegateFunction<'T, 'R>(d: Delegate) =
    inherit FSharpFunc<'T, 'R>()
    override _.Invoke(t: 'T) : 'R =
        let n = d.GetType().GetMethod("Invoke").GetParameters().Length   // not d.Method: an interpreted delegate's is synthetic
        let args =
            if n = 0 then [||]
            elif n > 1 && FSharp.Reflection.FSharpType.IsTuple typeof<'T> then FSharp.Reflection.FSharpValue.GetTupleFields(box t)
            else [| box t |]
        unbox<'R> (d.DynamicInvoke args)

[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
module DelegateFunction =
    let Make (funcType: Type) (d: Delegate) : obj =
        match FunctionShapes.domains funcType with
        | Some(domains, false, result) when domains.Length > 1 -> CurriedInvoker.buildWith domains result (fun args -> d.DynamicInvoke(Array.ofList args))
        | _ ->
            let ga = funcType.GetGenericArguments()
            Activator.CreateInstance(typedefof<TupledDelegateFunction<_, _>>.MakeGenericType(ga.[0], ga.[1]), [| box d |])

/// Delegates over F# functions, for an F# function argument passed to a delegate-typed parameter:
/// one adapter per shape whose `Invoke` method has the delegate's exact signature, so
/// `Delegate.CreateDelegate` binds it directly. Real methods rather than an `Expression.Lambda`
/// inside the rule: on Mono's browser-wasm runtime a nested interpreted lambda loses its arguments
/// (the same fault DlrRuntime's comment describes), and these are also faster.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
module FunctionAdapters =
    type Curried0<'R>(f: unit -> 'R) =
        member _.Invoke() : 'R = f ()
    type Curried0Unit(f: unit -> unit) =
        member _.Invoke() : unit = f ()
    type Curried1<'A, 'R>(f: 'A -> 'R) =
        let f = f
        member _.Invoke(a: 'A) : 'R = f a
    type Curried1Unit<'A>(f: 'A -> unit) =
        let f = f
        member _.Invoke(a: 'A) : unit = f a
    type Curried2<'A, 'B, 'R>(f: 'A -> 'B -> 'R) =
        let f = OptimizedClosures.FSharpFunc<'A, 'B, 'R>.Adapt f
        member _.Invoke(a: 'A, b: 'B) : 'R = f.Invoke(a, b)
    type Curried2Unit<'A, 'B>(f: 'A -> 'B -> unit) =
        let f = OptimizedClosures.FSharpFunc<'A, 'B, unit>.Adapt f
        member _.Invoke(a: 'A, b: 'B) : unit = f.Invoke(a, b)
    type Tupled2<'A, 'B, 'R>(f: 'A * 'B -> 'R) =
        member _.Invoke(a: 'A, b: 'B) : 'R = f (a, b)
    type Tupled2Unit<'A, 'B>(f: 'A * 'B -> unit) =
        member _.Invoke(a: 'A, b: 'B) : unit = f (a, b)
    type Curried3<'A, 'B, 'C, 'R>(f: 'A -> 'B -> 'C -> 'R) =
        let f = OptimizedClosures.FSharpFunc<'A, 'B, 'C, 'R>.Adapt f
        member _.Invoke(a: 'A, b: 'B, c: 'C) : 'R = f.Invoke(a, b, c)
    type Curried3Unit<'A, 'B, 'C>(f: 'A -> 'B -> 'C -> unit) =
        let f = OptimizedClosures.FSharpFunc<'A, 'B, 'C, unit>.Adapt f
        member _.Invoke(a: 'A, b: 'B, c: 'C) : unit = f.Invoke(a, b, c)
    type Tupled3<'A, 'B, 'C, 'R>(f: 'A * 'B * 'C -> 'R) =
        member _.Invoke(a: 'A, b: 'B, c: 'C) : 'R = f (a, b, c)
    type Tupled3Unit<'A, 'B, 'C>(f: 'A * 'B * 'C -> unit) =
        member _.Invoke(a: 'A, b: 'B, c: 'C) : unit = f (a, b, c)
    type Curried4<'A, 'B, 'C, 'D, 'R>(f: 'A -> 'B -> 'C -> 'D -> 'R) =
        let f = OptimizedClosures.FSharpFunc<'A, 'B, 'C, 'D, 'R>.Adapt f
        member _.Invoke(a: 'A, b: 'B, c: 'C, d: 'D) : 'R = f.Invoke(a, b, c, d)
    type Curried4Unit<'A, 'B, 'C, 'D>(f: 'A -> 'B -> 'C -> 'D -> unit) =
        let f = OptimizedClosures.FSharpFunc<'A, 'B, 'C, 'D, unit>.Adapt f
        member _.Invoke(a: 'A, b: 'B, c: 'C, d: 'D) : unit = f.Invoke(a, b, c, d)
    type Tupled4<'A, 'B, 'C, 'D, 'R>(f: 'A * 'B * 'C * 'D -> 'R) =
        member _.Invoke(a: 'A, b: 'B, c: 'C, d: 'D) : 'R = f (a, b, c, d)
    type Tupled4Unit<'A, 'B, 'C, 'D>(f: 'A * 'B * 'C * 'D -> unit) =
        member _.Invoke(a: 'A, b: 'B, c: 'C, d: 'D) : unit = f (a, b, c, d)
    type Curried5<'A, 'B, 'C, 'D, 'E, 'R>(f: 'A -> 'B -> 'C -> 'D -> 'E -> 'R) =
        let f = OptimizedClosures.FSharpFunc<'A, 'B, 'C, 'D, 'E, 'R>.Adapt f
        member _.Invoke(a: 'A, b: 'B, c: 'C, d: 'D, e: 'E) : 'R = f.Invoke(a, b, c, d, e)
    type Curried5Unit<'A, 'B, 'C, 'D, 'E>(f: 'A -> 'B -> 'C -> 'D -> 'E -> unit) =
        let f = OptimizedClosures.FSharpFunc<'A, 'B, 'C, 'D, 'E, unit>.Adapt f
        member _.Invoke(a: 'A, b: 'B, c: 'C, d: 'D, e: 'E) : unit = f.Invoke(a, b, c, d, e)
    type Tupled5<'A, 'B, 'C, 'D, 'E, 'R>(f: 'A * 'B * 'C * 'D * 'E -> 'R) =
        member _.Invoke(a: 'A, b: 'B, c: 'C, d: 'D, e: 'E) : 'R = f (a, b, c, d, e)
    type Tupled5Unit<'A, 'B, 'C, 'D, 'E>(f: 'A * 'B * 'C * 'D * 'E -> unit) =
        member _.Invoke(a: 'A, b: 'B, c: 'C, d: 'D, e: 'E) : unit = f (a, b, c, d, e)

    let private adapters = System.Collections.Concurrent.ConcurrentDictionary<struct (Type * Type), ConstructorInfo>()

    /// The adapter constructor for a function value's runtime shape and a delegate type, or None
    /// when they do not match: same arity, the function's domains reference-assignable to the
    /// delegate's parameters, a `unit` result only for a void delegate.
    let tryAdapter (funcType: Type) (delegateType: Type) : ConstructorInfo option =
        match adapters.TryGetValue(struct (funcType, delegateType)) with
        | true, c -> Some c
        | _ ->
            let invoke = delegateType.GetMethod("Invoke")
            let ps = [ for p in invoke.GetParameters() -> p.ParameterType ]
            let isVoid = invoke.ReturnType = typeof<Void>
            match FunctionShapes.domains funcType with
            | Some(ds, tupled, result) ->
                let ds = if ds = [ typeof<unit> ] then [] else ds
                let n = ds.Length
                let unitResult = result = typeof<unit>
                let fitsParams = n = ps.Length && List.forall2 (fun (d: Type) (p: Type) -> d = p || (not d.IsValueType && d.IsAssignableFrom p)) ds ps
                let fitsResult = if isVoid then unitResult else not unitResult && (invoke.ReturnType = result || (not result.IsValueType && invoke.ReturnType.IsAssignableFrom result))
                if not (fitsParams && fitsResult) || n > 5 || (tupled && n < 2) then None
                else
                    let name = (if tupled then "Tupled" else "Curried") + string n + (if isVoid then "Unit" else "")
                    let def = typeof<Curried0Unit>.DeclaringType.GetNestedType(name + (if n + (if isVoid then 0 else 1) > 0 then "`" + string (n + (if isVoid then 0 else 1)) else ""))
                    let closed = if def.IsGenericTypeDefinition then def.MakeGenericType(Array.ofList (ds @ (if isVoid then [] else [ result ]))) else def
                    let c = closed.GetConstructors().[0]
                    adapters.[struct (funcType, delegateType)] <- c
                    Some c
            | None -> None

    /// The delegate over `f`, built per call from its adapter.
    let Make (delegateType: Type) (f: obj) : Delegate =
        match tryAdapter (f.GetType()) delegateType with
        | Some c -> Delegate.CreateDelegate(delegateType, c.Invoke [| f |], "Invoke")
        | None -> raise (RuntimeBinderException(sprintf "Cannot convert an F# function of type '%s' to '%s'" (f.GetType().Name) delegateType.Name))

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

    let private isNullValue (a: DynamicMetaObject) = a.HasValue && isNull a.Value

    /// A concrete delegate type: `Delegate` and `MulticastDelegate` themselves have no `Invoke`.
    let private isDelegate (t: Type) = typeof<Delegate>.IsAssignableFrom t && not (isNull (t.GetMethod "Invoke"))

    /// An F# function value for a delegate-typed parameter: a delegate over an adapter of the
    /// function's shape (`FunctionAdapters`), as F# itself converts a lambda argument to a
    /// `Func`/`Action` parameter at a static call.
    let private functionToDelegate (delegateType: Type) (a: DynamicMetaObject) : Expression option =
        match FunctionAdapters.tryAdapter a.LimitType delegateType with
        | None -> None
        | Some _ ->
            let make = typeof<FunctionAdapters.Curried0Unit>.DeclaringType.GetMethod("Make")
            Some(Expression.Convert(Expression.Call(make, Expression.Constant delegateType, Expression.Convert(a.Expression, typeof<obj>)), delegateType) :> Expression)

    /// A delegate for an F# function-typed parameter: `DelegateFunction` (`DynamicInvoke`, any
    /// shape; not `FuncConvert`, whose wrapper loses arguments on Mono's browser-wasm runtime).
    let private delegateToFunction (funcType: Type) (a: DynamicMetaObject) : Expression option =
        let dt = a.LimitType
        let invoke = dt.GetMethod("Invoke")
        let paramTypes = [ for p in invoke.GetParameters() -> p.ParameterType ]
        // `unit -> R` takes a parameterless delegate.
        let domainsOf = FunctionShapes.domains funcType |> Option.map (fun (ds, tupled, r) -> (if ds = [ typeof<unit> ] then [] else ds), tupled, r)
        match domainsOf with
        | Some(domains, _, result) when domains.Length = paramTypes.Length && List.forall2 (fun (d: Type) (p: Type) -> d = p) domains paramTypes
                                          && (invoke.ReturnType = result || (invoke.ReturnType = typeof<Void> && result = typeof<unit>)) ->
            let make = typeof<TupledDelegateFunction<obj, obj>>.Assembly.GetType("FSharp.Interop.Dlr.DelegateFunction").GetMethod("Make")
            Some(Expression.Convert(Expression.Call(make, Expression.Constant funcType, Expression.Convert(a.Expression, typeof<Delegate>)), funcType) :> Expression)
        | _ -> None

    /// The argument converted to the parameter type, or None if it does not fit: assignable or
    /// C#-widened, a null for a reference slot, a bare value for an optional as `Some`, an F#
    /// function for a delegate parameter or a delegate for a function parameter.
    let private fit (p: ParameterInfo) (a: DynamicMetaObject) : Expression option =
        let pt = p.ParameterType
        let at = a.LimitType
        let converted (toType: Type) = Expression.Convert(Expression.Convert(a.Expression, at), toType) :> Expression
        if isNullValue a then
            if pt.IsValueType && isNull (Nullable.GetUnderlyingType pt) then None
            else Some(Expression.Constant(null, pt) :> Expression)
        elif Conversions.fits pt at then Some(converted pt)
        elif isOptional p && Conversions.fits (pt.GetGenericArguments().[0]) at then
            let inner = pt.GetGenericArguments().[0]
            Some(Expression.Call(pt.GetMethod("Some"), converted inner) :> Expression)
        elif isDelegate pt && (FunctionShapes.domains at).IsSome then functionToDelegate pt a
        elif isDelegate at && (FunctionShapes.domains pt).IsSome then delegateToFunction pt a
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
            let restrict (a: DynamicMetaObject) =
                if isNullValue a then BindingRestrictions.GetInstanceRestriction(a.Expression, null)
                else BindingRestrictions.GetTypeRestriction(a.Expression, a.LimitType)
            let restrictions =
                Array.fold (fun (r: BindingRestrictions) a -> r.Merge(restrict a)) (BindingRestrictions.GetTypeRestriction(target.Expression, t)) args
            Some(DynamicMetaObject(value, restrictions))
        | [] -> None

/// Equality and ordering with F# semantics where C# has none: records, unions, tuples, lists,
/// options, sets and any other type without the CLR operator get `=`/`compare` (structural,
/// through `LanguagePrimitives`) instead of C#'s reference equality or "operator cannot be
/// applied". Types C# handles itself — primitives, enums, strings, delegates, and any type that
/// declares the operator — keep C#'s binding, as do all non-comparison operators.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpBinaryOperationBinder(csharp: BinaryOperationBinder) =
    inherit BinaryOperationBinder(csharp.Operation)

    static let operatorName =
        dict [ ExpressionType.Equal, "op_Equality"; ExpressionType.NotEqual, "op_Inequality"
               ExpressionType.LessThan, "op_LessThan"; ExpressionType.LessThanOrEqual, "op_LessThanOrEqual"
               ExpressionType.GreaterThan, "op_GreaterThan"; ExpressionType.GreaterThanOrEqual, "op_GreaterThanOrEqual" ]

    static let equality =
        match <@ LanguagePrimitives.GenericEquality (box 1) (box 2) @> with
        | Patterns.Call(_, mi, _) -> mi
        | _ -> failwith "unreachable"

    static let comparison =
        match <@ LanguagePrimitives.GenericComparison (box 1 :?> IComparable) (box 2 :?> IComparable) @> with
        | Patterns.Call(_, mi, _) -> mi.GetGenericMethodDefinition().MakeGenericMethod typeof<obj>
        | _ -> failwith "unreachable"

    /// A type C#'s own operators cover, or that binds for itself (a dynamic object, whose own
    /// rule reaches us as the error suggestion through C#). Strings and bools have C# equality
    /// but no C# ordering, so `<` on them is F#'s (ordinal; `false < true`).
    static let native (op: ExpressionType) (t: Type) =
        let t = match Nullable.GetUnderlyingType t with null -> t | u -> u
        let equality = op = ExpressionType.Equal || op = ExpressionType.NotEqual
        (t.IsPrimitive && (equality || t <> typeof<bool>)) || t.IsEnum || t = typeof<decimal>
        || typeof<Delegate>.IsAssignableFrom t || typeof<IDynamicMetaObjectProvider>.IsAssignableFrom t
        || (t = typeof<string> && equality)

    static let declares (name: string) (t: Type) =
        t.GetMethods(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.FlattenHierarchy)
        |> Array.exists (fun m -> m.Name = name && m.GetParameters().Length = 2)

    let isNullValue (a: DynamicMetaObject) = a.HasValue && isNull a.Value

    /// Whether the operation is one this binder may bind structurally.
    static member IsComparison(op: ExpressionType) = operatorName.ContainsKey op

    override this.FallbackBinaryOperation(target, arg, errorSuggestion) =
        let structural =
            match operatorName.TryGetValue this.Operation with
            | true, name ->
                let operands = [ target; arg ] |> List.filter (fun a -> not (isNullValue a)) |> List.map (fun a -> a.LimitType)
                not operands.IsEmpty && operands |> List.forall (fun t -> not (native this.Operation t) && not (declares name t))
            | _ -> false
        if not structural then csharp.FallbackBinaryOperation(target, arg, errorSuggestion)
        else
            let boxed (a: DynamicMetaObject) = Expression.Convert(a.Expression, typeof<obj>) :> Expression
            let l, r = boxed target, boxed arg
            let value : Expression =
                match this.Operation with
                | ExpressionType.Equal -> Expression.Call(equality, l, r)
                | ExpressionType.NotEqual -> Expression.Not(Expression.Call(equality, l, r))
                | op ->
                    let c = Expression.Call(comparison, l, r)
                    let zero = Expression.Constant 0
                    match op with
                    | ExpressionType.LessThan -> Expression.LessThan(c, zero)
                    | ExpressionType.LessThanOrEqual -> Expression.LessThanOrEqual(c, zero)
                    | ExpressionType.GreaterThan -> Expression.GreaterThan(c, zero)
                    | _ -> Expression.GreaterThanOrEqual(c, zero)
            let restrict (a: DynamicMetaObject) =
                if isNullValue a then BindingRestrictions.GetInstanceRestriction(a.Expression, null)
                else BindingRestrictions.GetTypeRestriction(a.Expression, a.LimitType)
            DynamicMetaObject(Expression.Convert(value, typeof<obj>), (restrict target).Merge(restrict arg))

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

    /// A CLR target: an accessible property or field of that name whose declared type is a fitting
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
        | None ->
            // A method of optional parameters only, which C# cannot call with none, as error suggestion.
            let suggestion =
                if target.HasValue then
                    match OptionalArguments.tryCall context t name target args with
                    | Some rule -> rule
                    | None -> errorSuggestion
                else errorSuggestion
            csharp.FallbackInvokeMember(target, args, suggestion)
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
    static member Curried5<'A, 'B, 'C, 'D, 'E, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, 'C, 'D, 'E, obj>>, convert: CallSite<Func<CallSite, obj, 'R>>, target: obj) : 'A -> 'B -> 'C -> 'D -> 'E -> 'R =
        fun a b c d e -> convert.Target.Invoke(convert, invoke.Target.Invoke(invoke, target, a, b, c, d, e))
    static member Tupled5<'A, 'B, 'C, 'D, 'E, 'R>(invoke: CallSite<Func<CallSite, obj, 'A, 'B, 'C, 'D, 'E, obj>>, convert: CallSite<Func<CallSite, obj, 'R>>, target: obj) : 'A * 'B * 'C * 'D * 'E -> 'R =
        fun (a, b, c, d, e) -> convert.Target.Invoke(convert, invoke.Target.Invoke(invoke, target, a, b, c, d, e))
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
    static member Curried5Unit<'A, 'B, 'C, 'D, 'E>(invoke: CallSite<Action<CallSite, obj, 'A, 'B, 'C, 'D, 'E>>, target: obj) : 'A -> 'B -> 'C -> 'D -> 'E -> unit =
        fun a b c d e -> invoke.Target.Invoke(invoke, target, a, b, c, d, e)
    static member Tupled5Unit<'A, 'B, 'C, 'D, 'E>(invoke: CallSite<Action<CallSite, obj, 'A, 'B, 'C, 'D, 'E>>, target: obj) : 'A * 'B * 'C * 'D * 'E -> unit =
        fun (a, b, c, d, e) -> invoke.Target.Invoke(invoke, target, a, b, c, d, e)

/// The call sites of an operation whose binder inputs are only known at run time (`(?) x name`
/// with `name` a variable): one set of sites per distinct key, created on first use from a
/// quotation template the translator built for the operation, and read back out of it. The
/// delegate that invokes them is compiled once, at translation time, with the sites as
/// parameters — so a new key costs the binders and sites (~µs, a few hundred bytes), not a
/// `LambdaExpression.Compile()`, and a repeated key costs one dictionary lookup. Bounded: at
/// `Capacity` entries the cache is cleared and refills, a miss being cheap.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type SiteCache<'Key when 'Key: equality>(template: 'Key -> Expr) =
    let entries = System.Collections.Concurrent.ConcurrentDictionary<'Key, CallSite[]>()

    /// The `CallSite` constants in a template's expression, one per distinct site, in traversal
    /// order — the order the translator's parameters follow.
    static member Sites(e: Expr) : CallSite list =
        let found = System.Collections.Generic.List<CallSite>()
        let rec walk (e: Expr) =
            match e with
            | Patterns.Value(v, _) ->
                match v with
                | :? CallSite as site when not (found |> Seq.exists (fun s -> obj.ReferenceEquals(s, site))) -> found.Add site
                | _ -> ()
            | ExprShape.ShapeVar _ -> ()
            | ExprShape.ShapeLambda(_, body) -> walk body
            | ExprShape.ShapeCombination(_, args) -> List.iter walk args
        walk e
        List.ofSeq found

    /// Entries kept per cache before it is cleared.
    static member val Capacity = 256 with get, set

    member _.Count = entries.Count

    member _.Get(key: 'Key) : CallSite[] =
        match entries.TryGetValue key with
        | true, sites -> sites
        | _ ->
            // Misses only: the admission (clear at capacity, then add) is one critical section,
            // so concurrent misses cannot each pass the check and push the count past the bound.
            lock entries (fun () ->
                match entries.TryGetValue key with
                | true, sites -> sites
                | _ ->
                    if entries.Count >= SiteCache<'Key>.Capacity then entries.Clear()
                    let sites = Array.ofList (SiteCache<'Key>.Sites(template key))
                    entries.[key] <- sites
                    sites)

    /// `sites.[i]`, for the quotation (array indexing has no direct quotation form the converter takes).
    static member At(sites: CallSite[], i: int) : CallSite = sites.[i]

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

    /// C#'s InvokeMember binder wrapped to apply F# function values (see FSharpInvokeMemberBinder)
    /// for positional, non-generic calls of any arity; otherwise C#'s binder as is.
    let private smartInvokeMember (context: Type) (name: string) (typeArgs: Type list) (discard: bool) (all: Arg list) : CallSiteBinder =
        let args = List.tail all
        let flags = if discard then CSharpBinderFlags.ResultDiscarded else CSharpBinderFlags.None
        let typeArgSeq = match typeArgs with [] -> null | ts -> ts :> seq<Type>
        let csharp = Binder.InvokeMember(flags, name, typeArgSeq, context, [ for a in all -> argInfo a ])
        let positional = args |> List.forall (fun a -> isNull a.Name)
        if not positional || not typeArgs.IsEmpty then csharp
        else
            // Discarded results too: the site is void-returning and the DLR drops the rule's value.
            let csharpInvoke = Binder.Invoke(flags, context, [ for a in all -> argInfo a ]) :?> InvokeBinder
            FSharpInvokeMemberBinder(context, name, csharp :?> InvokeMemberBinder, csharpInvoke) :> CallSiteBinder

    /// `x?Name(args)` whose inferred type is `A -> R`: InvokeMember, applying an F# function value
    /// held by the member when C# cannot invoke it.
    let invokeMemberOrApply (context: Type) (name: string) (typeArgs: Type list) (discard: bool) (target: Arg) (args: Arg list) =
        let all = target :: args
        siteCall (smartInvokeMember context name typeArgs discard all) all (if discard then voidType else typeof<obj>)

    /// `Dlr.new'<T>(args)`: C#'s `new T(…)` with the constructor chosen by the arguments'
    /// runtime types. The type goes in as argument 0 of the site, flagged as a static type,
    /// exactly as the C# compiler emits it.
    let invokeConstructor (context: Type) (t: Type) (args: Arg list) =
        let typeArg = { Expr = Expr.Value(t, typeof<Type>); Type = typeof<Type>; Flags = CSharpArgumentInfoFlags.UseCompileTimeType ||| CSharpArgumentInfoFlags.IsStaticType; Name = null }
        let all = typeArg :: args
        siteCall (Binder.InvokeConstructor(CSharpBinderFlags.None, context, [ for a in all -> argInfo a ])) all typeof<obj>

    /// `Dlr.call args target`, applying `target` itself when it is an F# function.
    let invokeOrApply (context: Type) (discard: bool) (target: Arg) (args: Arg list) =
        let all = target :: args
        let csharp = Binder.Invoke((if discard then CSharpBinderFlags.ResultDiscarded else CSharpBinderFlags.None), context, [ for a in all -> argInfo a ])
        let positional = args |> List.forall (fun a -> isNull a.Name)
        let binder =
            if not positional then csharp
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
        if argTypes.Length > 5 && not tupled then
            // Beyond the typed helpers: a run-time-built curried closure (CurriedInvoker), which is
            // what F# itself does past OptimizedClosures, with DynamicInvoke at the end.
            let convertSite = if discard then Expr.Value(null, typeof<CallSite>) else site (Binder.Convert(CSharpBinderFlags.None, resultType, context)) [ dynamicArg (Expr.Value(null, typeof<obj>)) ] resultType
            let mi = typeof<CurryStep<obj, obj>>.Assembly.GetType("FSharp.Interop.Dlr.CurriedInvoker").GetMethod("build")
            let call =
                Expr.Call(mi, [ Expr.Value(argTypes, typeof<Type list>); Expr.Value((if discard then typeof<unit> else resultType), typeof<Type>)
                                Expr.Coerce(invokeSite, typeof<CallSite>); Expr.Coerce(convertSite, typeof<CallSite>); target.Expr ])
            Expr.Coerce(call, functionType)
        elif argTypes.Length > 5 then
            raise (DlrTranslationException(
                    sprintf "dlr { } can read a member as a tupled function of up to five elements; '%s' is read as one of %d. Read it curried, or call it: x?%s(…)." name argTypes.Length name))
        elif discard then
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
