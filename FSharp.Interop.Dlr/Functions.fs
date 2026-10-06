namespace FSharp.Interop.Dlr

open System
open System.Diagnostics.CodeAnalysis
open System.Dynamic
open System.Linq.Expressions
open System.Reflection
open System.Runtime.CompilerServices
open Microsoft.CSharp.RuntimeBinder
open FSharp.Quotations

/// Whether a function type (the runtime type of a value, or a member's declared type) is an
/// `FSharpFunc` that the call site's arguments fit, and the expression applying it. The shape
/// comes from the function itself, not from the call's declared result: a discarded result or a
/// widened one still applies the function that is there. Any arity: a curried function is a
/// chain of `Invoke` calls, a tupled one a tuple construction and one `Invoke`.
module internal FunctionShapes =
    let private isFunc (t: Type) = t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<FSharpFunc<_, _>>

    let private fits (paramType: Type) (argType: Type) = Conversions.fits paramType argType

    /// A conversion for an argument that does not fit a domain by assignment or widening — an
    /// F# function for a delegate domain, a delegate for a function domain — supplied by
    /// `Fallback` once it exists (it is in `Seam.fs`, compiled after this file, and uses `applyCall`
    /// itself, for the largest delegates). None until then, and None when nothing applies.
    let mutable convertArgument : Type -> DynamicMetaObject -> Expression option = fun _ _ -> None

    let isNullValue (a: DynamicMetaObject) = a.HasValue && isNull a.Value

    /// The restriction a rule carries for one argument: its runtime type, or for a null value
    /// (a null reference, or `unit`, whose value is null) the instance: a type restriction can
    /// never hold for null, and a rule that fails its own test makes the DLR re-bind forever.
    let restrictArg (a: DynamicMetaObject) =
        if isNullValue a then BindingRestrictions.GetInstanceRestriction(a.Expression, null)
        else BindingRestrictions.GetTypeRestriction(a.Expression, a.LimitType)

    /// A null value fits any reference-type domain but `unit` whatever its static type (an untyped
    /// `null` is `obj`), as C# lets `null` go to any reference parameter; the rule then carries an
    /// instance restriction for it (`restrictions`). Not `unit`: that would let a one-argument
    /// call bind a zero-argument function because the argument happened to be null.
    let private fitsNull (domain: Type) (a: DynamicMetaObject) =
        isNullValue a && not domain.IsValueType && domain <> typeof<unit>

    let private fitsArg (domain: Type) (a: DynamicMetaObject) =
        fits domain a.LimitType || fitsNull domain a || (convertArgument domain a).IsSome

    /// The `FSharpFunc<_, _>` a type is or derives from: a function value's runtime type is a
    /// compiler-generated subclass (`f@12`), a member's declared type usually the base itself.
    let rec funcBase (t: Type) : Type option =
        if isNull t then None
        elif isFunc t then Some t
        else funcBase t.BaseType

    let private invoke (f: Expression) (funcType: Type) (arg: Expression) : Expression =
        Expression.Call(f, funcType.GetMethod("Invoke"), arg) :> Expression

    /// Through the LimitType first: an `obj`-typed argument is unboxed as its runtime type, then
    /// widened; converting `obj` straight to `int64` would unbox a boxed `int` as `int64` and throw.
    let private convertTo (t: Type) (a: DynamicMetaObject) =
        if fits t a.LimitType then
            let unboxed = if a.Expression.Type = a.LimitType then a.Expression else Expression.Convert(a.Expression, a.LimitType) :> Expression
            if a.LimitType = t then unboxed else Expression.Convert(unboxed, t) :> Expression
        elif fitsNull t a then Expression.Convert(a.Expression, t) :> Expression
        else (convertArgument t a).Value

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

    /// `domains` as a delegate's parameters: a `unit -> R` takes none (a parameterless delegate).
    let parameters (funcType: Type) : (Type list * bool * Type) option =
        domains funcType |> Option.map (fun (ds, tupled, result) -> (if ds = [ typeof<unit> ] then [] else ds), tupled, result)

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
            | [ a ] when fitsArg domain a -> Some(boxed (invoke f ft (convertTo domain a)))
            | [] | [ _ ] -> None
            | args ->
                let n = args.Length
                let tupled =
                    if FSharp.Reflection.FSharpType.IsTuple domain then
                        let es = List.ofArray (FSharp.Reflection.FSharpType.GetTupleElements domain)
                        if es.Length = n && List.forall2 fitsArg es args then
                            let tuple = Tuples.newExpression domain (List.map2 convertTo es args)
                            Some(boxed (invoke f ft tuple))
                        else None
                    else None
                match tupled with
                | Some call -> Some call
                | None ->
                    // Curried: each domain in turn must fit. Up to five arguments `InvokeFast` applies
                    // them in one call (no intermediate closures, as F# compiles `f a b`); past that
                    // each step's result is the next function.
                    match domains funcType with
                    | Some(ds, false, result) when ds.Length = n && n <= 5 && List.forall2 fitsArg ds args ->
                        // `FSharpFunc<T, U>.InvokeFast<V, …>(func, t, u, v, …)`: the declaring type takes the
                        // first two domains, the method's own type parameters the rest and the result.
                        let declaring = typedefof<FSharpFunc<_, _>>.MakeGenericType(ds.[0], ds.[1])
                        let fast =
                            declaring.GetMethods()
                            |> Array.find (fun m -> m.Name = "InvokeFast" && m.GetParameters().Length = n + 1)
                            |> fun m -> m.MakeGenericMethod(Array.ofList (List.skip 2 ds @ [ result ]))
                        let funcArg = Expression.Convert(read, fast.GetParameters().[0].ParameterType) :> Expression
                        Some(boxed (Expression.Call(fast, funcArg :: List.map2 convertTo ds args)))
                    | _ ->
                        let rec chain (fe: Expression) (t: Type) (remaining: DynamicMetaObject list) =
                            match remaining with
                            | [] -> Some fe
                            | a :: rest ->
                                match funcBase t with
                                | Some ft when fitsArg (ft.GetGenericArguments().[0]) a ->
                                    let ga = ft.GetGenericArguments()
                                    chain (invoke (Expression.Convert(fe, ft)) ft (convertTo ga.[0] a)) ga.[1] rest
                                | _ -> None
                        chain read funcType args |> Option.map boxed

    /// The rule's restrictions: the function's runtime type and each argument's (`restrictArg`).
    let restrictions (target: DynamicMetaObject) (targetType: Type) (args: DynamicMetaObject[]) =
        Array.fold (fun (r: BindingRestrictions) (a: DynamicMetaObject) -> r.Merge(restrictArg a))
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
                && (let g = p.GetGetMethod(true) in not (isNull g) && Accessibility.method' context t g))
        match property with
        | Some p -> Some(p.PropertyType, Expression.Property(self, p) :> Expression)
        | None ->
            match t.GetFields(Accessibility.all) |> Array.tryFind (fun f -> f.Name = name && Accessibility.field context t f) with
            | None -> None
            | Some f -> Some(f.FieldType, Expression.Field(self, f) :> Expression)

    /// Whether a member's declared type says nothing useful about whether it holds a function:
    /// `obj`, an interface, an abstract class. Its value then goes through a nested site.
    let opaque (t: Type) = t = typeof<obj> || t.IsInterface || (t.IsAbstract && not t.IsSealed)

/// One step of a function `FunctionBuilder` builds: the step before it (`Prev`; for the first, the
/// captured values as a `Tuple`), the argument that step took (`Own`; for the first, null), and
/// `next`, compiled once, which takes this step with its argument — the whole tuple, for a tupled
/// function — to the next step or, at the last, the call, reading the arguments back along `Prev`.
/// One object per step, no closure and nothing copied. Everything it holds is a class or a
/// domain's own type: a struct over references as a type argument here (a `ValueTuple`
/// accumulator) put each step on the runtime's slow shared-generic path, three times the cost. A
/// class of its own rather than `FuncConvert`, whose wrappers lose their argument on Mono's
/// interpreter (wasm).
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type CurriedStep<'Prev, 'Own, 'A, 'R>(prev: 'Prev, own: 'Own, next: Func<CurriedStep<'Prev, 'Own, 'A, 'R>, 'A, 'R>) =
    inherit FSharpFunc<'A, 'R>()
    member _.Prev = prev
    member _.Own = own
    override this.Invoke(a: 'A) : 'R = next.Invoke(this, a)

/// F# functions past five arguments as LINQ, compiled once per (function type, site type) — or
/// per (function type, delegate type): tupled, one `CurriedStep` taking the whole tuple, its
/// elements read through `Rest`; curried, a `CurriedStep` per argument — what F# emits for a
/// curried function beyond OptimizedClosures, minus InvokeFast. Typed throughout: no boxing, no `DynamicInvoke`, and what
/// the call throws arrives as itself.
module internal FunctionBuilder =
    /// The F# function whose result is `call captured args`: tupled over `tupleType` when given
    /// (`domains` its elements), else curried over `domains`; `captured` (one to seven: sites,
    /// target, delegate) are values from the enclosing lambda, handed to `call` as expressions
    /// valid where it is placed. `resultType` is the function's final result, which `call` produces.
    let build (tupleType: Type option) (domains: Type list) (resultType: Type) (captured: Expression list)
              (call: Expression list -> Expression list -> Expression) : Expression =
        let capturedType = typedefof<Tuple<_>>.Assembly.GetType(sprintf "System.Tuple`%d" captured.Length).MakeGenericType([| for e in captured -> e.Type |])
        // The steps' argument types: the tuple alone, or each argument.
        let steps = match tupleType with Some t -> [ t ] | None -> domains
        let n = steps.Length
        let rec funcType k = if k = n then resultType else typedefof<FSharpFunc<_, _>>.MakeGenericType(steps.[k], funcType (k + 1))
        let rec stepType k =
            let prev, own = if k = 0 then capturedType, typeof<obj> else stepType (k - 1), steps.[k - 1]
            typedefof<CurriedStep<_, _, _, _>>.MakeGenericType(prev, own, steps.[k], funcType (k + 1))
        let newStep k (prev: Expression) (own: Expression) (next: Delegate) =
            Expression.Convert(Expression.New((stepType k).GetConstructors().[0], prev, own, Expression.Constant(next)), funcType k) :> Expression
        // `next` of step k: on to step k+1, or, at the last, the call.
        let rec next k : Delegate =
            let self = Expression.Parameter(stepType k, "self")
            let a = Expression.Parameter(steps.[k], "a")
            let body =
                if k < n - 1 then newStep (k + 1) self a (next (k + 1))
                else
                    // Step j is n-1-j `Prev`s up; argument i is step i+1's `Own`.
                    let rec up (j: int) : Expression = if j = n - 1 then self else Expression.Property(up (j + 1), "Prev")
                    let capturedValues = [ for i in 0 .. captured.Length - 1 -> Expression.Property(Expression.Property(up 0, "Prev"), sprintf "Item%d" (i + 1)) :> Expression ]
                    let args =
                        match tupleType with
                        | Some _ -> [ for i in 0 .. domains.Length - 1 -> Tuples.element a i ]
                        | None -> [ for i in 0 .. n - 2 -> Expression.Property(up (i + 1), "Own") :> Expression ] @ [ a ]
                    call capturedValues args
            Expression.Lambda(typedefof<Func<_, _, _>>.MakeGenericType(stepType k, steps.[k], funcType (k + 1)), body, "dlr-curriedStep", [ self; a ]).Compile()
        newStep 0 (Expression.New(capturedType.GetConstructors().[0], captured)) (Expression.Constant(null, typeof<obj>)) (next 0)

    /// A void call as an F# `unit` result.
    let unitOf (call: Expression) : Expression =
        if call.Type = typeof<Void> then Expression.Block(typeof<unit>, call, Expression.Constant(null, typeof<unit>)) :> Expression else call

/// The typed wrapper (`DelegateFunctions`, in Adapters.fs) for a delegate passed to a
/// function-typed parameter.
module internal DelegateConversions =
    let private makers = System.Collections.Concurrent.ConcurrentDictionary<struct (Type * Type), (Delegate -> obj) option>(TypePairComparer.Instance)

    /// A maker of the typed wrapper for a function type from a delegate type, or None when the
    /// signatures do not fit (`Signatures.delegateServesFunction`) — then the binder does not offer the conversion
    /// (`delegateToFunction`), and None is cached like a maker. The delegate is rebound to the
    /// `Func`/`Action` of the function's signature over its own `Invoke`; past five
    /// parameters, a factory compiled once calls the delegate type's own `Invoke`.
    let tryTyped (funcType: Type) (delegateType: Type) : (Delegate -> obj) option =
        match makers.TryGetValue(struct (funcType, delegateType)) with
        | true, m -> m
        | _ ->
            let invoke = DelegateMembers.invokeOf delegateType
            let ps = [ for p in invoke.GetParameters() -> p.ParameterType ]
            let isVoid = invoke.ReturnType = typeof<Void>
            let maker =
                match FunctionShapes.parameters funcType with
                | Some(ds, tupled, result) ->
                    let n = ds.Length
                    if not (Signatures.delegateServesFunction ds result ps invoke.ReturnType) || (tupled && n < 2) then None
                    elif n > 5 then
                        // Past the typed wrappers: a factory compiled once (FunctionBuilder) calling
                        // this delegate type's own Invoke.
                        let d = Expression.Parameter(typeof<Delegate>, "d")
                        let typed = Expression.Convert(d, delegateType)
                        let body =
                            FunctionBuilder.build (if tupled then Some (funcType.GetGenericArguments().[0]) else None) ds result [ typed ]
                                (fun captured args -> FunctionBuilder.unitOf (Expression.Call(List.head captured, invoke, args)))
                        let factory = Expression.Lambda<Func<Delegate, obj>>(Expression.Convert(body, typeof<obj>), "dlr-delegateToFunction", [ d ]).Compile()
                        Some factory.Invoke
                    else
                        let standard = if isVoid then Expression.GetActionType(Array.ofList ds) else Expression.GetFuncType(Array.ofList (ds @ [ result ]))
                        let name = (if tupled then "Tupled" else "") + (if isVoid then "Action" else "Func") + string n
                        let def = typeof<DelegateFunctions.Action0<unit>>.DeclaringType.GetNestedType(name + "`" + string (n + 1))
                        let closed = def.MakeGenericType(Array.ofList (ds @ [ result ]))
                        let ctor = closed.GetConstructors().[0]
                        // `new Wrapper(d)`.
                        let construct =
                            Emit.factory<Func<Delegate, obj>> "wrap" typeof<obj> [| typeof<Delegate> |] closed.Module
                                (fun il ->
                                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0)
                                    il.Emit(System.Reflection.Emit.OpCodes.Castclass, standard)
                                    il.Emit(System.Reflection.Emit.OpCodes.Newobj, ctor)
                                    il.Emit(System.Reflection.Emit.OpCodes.Ret))
                                (fun () -> Func<Delegate, obj>(fun d -> ctor.Invoke [| box d |]))
                        Some(fun (d: Delegate) ->
                            // Rebound over the delegate's own `Invoke`, not its last target and method:
                            // a multicast delegate keeps every target.
                            let standardDelegate = if d.GetType() = standard then d else Delegate.CreateDelegate(standard, d, DelegateMembers.invokeOf (d.GetType()))
                            construct.Invoke standardDelegate)
                | None -> None
            makers.[struct (funcType, delegateType)] <- maker
            maker


/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
module DelegateFunction =
    /// The F# function of `funcType` over `d` — always a typed wrapper: the binder offers the
    /// conversion only when `DelegateConversions.tryTyped` has one (`delegateToFunction`).
    let Make (funcType: Type) (d: Delegate) : obj =
        match DelegateConversions.tryTyped funcType (d.GetType()) with
        | Some make -> make d
        | None -> invalidOp (sprintf "No conversion of %s to %s: the binder offers only those tryTyped makes." (d.GetType().Name) funcType.Name)

    /// `Make` as a method, for the expression tree of a bound call to name it.
    let makeMethod : MethodInfo = Quotation.methodOf <@ Make typeof<obj> null @>


/// A delegate literal written inside a block (`w?Each(Action<string>(fun s -> …))`) compiles with
/// the block, as a `DynamicMethod` delegate whose `.Method` has a hidden `Closure` first parameter;
/// a consumer marshalling by `.Method` (NLua, some event-wiring helpers) sees `(Closure, string)`
/// and refuses it. `Over` re-wraps it on the delegate type's own `Invoke`, so `.Method` is honest and
/// `.Target` the inner delegate — one indirection per call — through a factory emitted once per
/// delegate type (`dup; ldvirtftn Invoke; newobj`), or `Delegate.CreateDelegate` where dynamic code is not
/// supported. The seam's past-sixteen-parameter conversion, a compiled lambda too, goes through
/// it as well. Public: compiled blocks call `Over`.
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type DelegateLiteral<'D when 'D :> Delegate> [<ExcludeFromCodeCoverage>] private () =
    static let factory : Func<'D, 'D> =
        let delegateType = typeof<'D>
        let invoke = DelegateMembers.invokeOf delegateType
        Emit.factory<Func<'D, 'D>> "rewrap" delegateType [| delegateType |] delegateType.Module
            (fun il ->
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0)
                il.Emit(System.Reflection.Emit.OpCodes.Dup)
                il.Emit(System.Reflection.Emit.OpCodes.Ldvirtftn, invoke)   // `Invoke` is virtual; the JIT accepts only `dup; ldvirtftn` before `newobj` here
                il.Emit(System.Reflection.Emit.OpCodes.Newobj, DelegateMembers.constructorOf delegateType)
                il.Emit(System.Reflection.Emit.OpCodes.Ret))
            (fun () -> Func<'D, 'D>(fun inner -> Delegate.CreateDelegate(delegateType, inner, invoke) :?> 'D))

    static member Over(inner: 'D) : 'D = factory.Invoke inner

    /// A literal compiled at a stand-in delegate type of the same signature (a Func or Action;
    /// see Translate's `NewDelegate`), as this type, and re-wrapped as `Over` does.
    static member From(inner: Delegate) : 'D =
        DelegateLiteral<'D>.Over(Delegate.CreateDelegate(typeof<'D>, inner, DelegateMembers.invokeOf (inner.GetType())) :?> 'D)

/// A parameterless delegate literal in a block (`Func<int>(fun () -> 7)`, `Action(fun () -> …)`,
/// #156): F# quotes it in a form no rewrite can rebuild, so the translator hands its body over as
/// the thunk `fun () -> body` and this makes the `'D` over it — by the literal's own type, which
/// the compiler fixed, not by the shape of the thunk (a `Func<unit>`, or a `Func<int -> int>` whose
/// result is itself a function, are what a run-time conversion would misread). A factory compiled
/// once per delegate type: `'D` calling the thunk, re-wrapped as `DelegateLiteral.Over` does, and
/// on .NET Framework built at the public stand-in for an `internal` type. Public: compiled blocks
/// call `Of`.
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type ParameterlessLiteral<'D, 'R when 'D :> Delegate> [<ExcludeFromCodeCoverage>] private () =
    static let factory : Func<FSharpFunc<unit, 'R>, 'D> =
        let delegateType = typeof<'D>
        let f = Expression.Parameter(typeof<FSharpFunc<unit, 'R>>, "f")
        let call = Expression.Call(f, typeof<FSharpFunc<unit, 'R>>.GetMethod("Invoke"), Expression.Constant(null, typeof<unit>)) :> Expression
        let invoke = DelegateMembers.invokeOf delegateType
        let body = if invoke.ReturnType = typeof<Void> then Expression.Block(typeof<Void>, [| call |]) :> Expression else call
        let honest =
            match DelegateMembers.standIn delegateType with
            | Some standIn -> Expression.Call(typeof<DelegateLiteral<'D>>.GetMethod("From"), Expression.Convert(Expression.Lambda(standIn, body), typeof<Delegate>))
            | None -> Expression.Call(typeof<DelegateLiteral<'D>>.GetMethod("Over"), Expression.Lambda(delegateType, body))
        Expression.Lambda<Func<FSharpFunc<unit, 'R>, 'D>>(honest, "dlr-parameterlessLiteral", [ f ]).Compile()
    static member Of(thunk: FSharpFunc<unit, 'R>) : 'D = factory.Invoke thunk

/// A delegate over an F# function (`FunctionAdapters`, in Adapters.fs): per (function type,
/// delegate type) a factory emitted once as IL — `new Adapter(f)` and the delegate constructor
/// over its `Invoke` — so a conversion costs an allocation, not `Delegate.CreateDelegate`'s
/// per-call validation (~300 ns) or a LINQ closure (about the same). Where dynamic code is not
/// supported, `CreateDelegate` it is. Past sixteen parameters (a custom delegate type), a
/// compiled lambda applying the function. Public: compiled blocks call `Make`.
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
module FunctionConversions =
    let private conversions = System.Collections.Concurrent.ConcurrentDictionary<struct (Type * Type), Func<obj, Delegate> option>(TypePairComparer.Instance)

    /// `fun x -> delegate ctor over Invoke` of `adapter` (a `FunctionAdapters` or `MemberInvokers`
    /// type): the instance `x` itself or, with `wrap`, `new adapter(x)`; as IL when the runtime
    /// allows it.
    let private emitBinding (delegateType: Type) (adapter: Type) (wrap: bool) : Func<obj, Delegate> =
        let ctor = adapter.GetConstructors().[0]
        let invoke = adapter.GetMethod("Invoke")
        Emit.factory<Func<obj, Delegate>> "make" typeof<Delegate> [| typeof<obj> |] adapter.Module
            (fun il ->
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0)
                if wrap then
                    il.Emit(System.Reflection.Emit.OpCodes.Castclass, ctor.GetParameters().[0].ParameterType)
                    il.Emit(System.Reflection.Emit.OpCodes.Newobj, ctor)
                else il.Emit(System.Reflection.Emit.OpCodes.Castclass, adapter)
                il.Emit(System.Reflection.Emit.OpCodes.Ldftn, invoke)
                il.Emit(System.Reflection.Emit.OpCodes.Newobj, DelegateMembers.constructorOf delegateType)
                il.Emit(System.Reflection.Emit.OpCodes.Ret))
            (fun () -> Func<obj, Delegate>(fun x -> Delegate.CreateDelegate(delegateType, (if wrap then ctor.Invoke [| x |] else x), invoke)))

    /// `fun f -> new Adapter(f) |> delegate ctor over Invoke`.
    let private emitFactory (delegateType: Type) (adapter: Type) : Func<obj, Delegate> = emitBinding delegateType adapter true

    let private bindings = System.Collections.Concurrent.ConcurrentDictionary<struct (Type * Type), Func<obj, Delegate>>(TypePairComparer.Instance)

    /// The factory binding `delegateType` to an instance of `instanceType` (a `MemberInvokers`
    /// type, whose `Invoke` has the delegate's signature), once per pair.
    let over (instanceType: Type) (delegateType: Type) : Func<obj, Delegate> =
        bindings.GetOrAdd(struct (instanceType, delegateType), fun _ -> emitBinding delegateType instanceType false)

    /// The factory for `delegateType` from a function value of `funcType`, or None when the shapes
    /// do not fit (`Signatures.functionServesDelegate`).
    let tryConversion (funcType: Type) (delegateType: Type) : Func<obj, Delegate> option =
        match conversions.TryGetValue(struct (funcType, delegateType)) with
        | true, c -> c
        | _ ->
            let invoke = DelegateMembers.invokeOf delegateType
            let ps = [ for p in invoke.GetParameters() -> p.ParameterType ]
            let isVoid = invoke.ReturnType = typeof<Void>
            let conversion =
                match FunctionShapes.parameters funcType with
                | Some(ds, tupled, result) ->
                    let n = ds.Length
                    if not (Signatures.functionServesDelegate ds result ps invoke.ReturnType) || (tupled && n < 2) then None
                    elif n <= 16 then
                        let name = (if tupled then "Tupled" else "Curried") + string n + (if isVoid then "Unit" else "")
                        let arity = n + (if isVoid then 0 else 1)
                        let def = typeof<FunctionAdapters.Curried0Unit>.DeclaringType.GetNestedType(name + (if arity > 0 then "`" + string arity else ""))
                        let closed = if def.IsGenericTypeDefinition then def.MakeGenericType(Array.ofList (ds @ (if isVoid then [] else [ result ]))) else def
                        Some(emitFactory delegateType closed)
                    else
                        let fParam = Expression.Parameter(typeof<obj>, "f")
                        let parameters = [| for p in invoke.GetParameters() -> Expression.Parameter(p.ParameterType, p.Name) |]
                        let args = [| for p in parameters -> DynamicMetaObject(p, BindingRestrictions.Empty) |]
                        FunctionShapes.applyCall funcType (Expression.Convert(fParam, funcType)) args
                        |> Option.map (fun call ->
                            let body =
                                if isVoid then Expression.Block(typeof<Void>, [| call |]) :> Expression
                                else Expression.Convert(call, invoke.ReturnType) :> Expression
                            let literalOf = typedefof<DelegateLiteral<_>>.MakeGenericType delegateType
                            let honest =
                                match DelegateMembers.standIn delegateType with
                                | Some standIn -> Expression.Call(literalOf.GetMethod("From"), Expression.Convert(Expression.Lambda(standIn, body, parameters), typeof<Delegate>))
                                | None -> Expression.Call(literalOf.GetMethod("Over"), Expression.Lambda(delegateType, body, parameters))
                            Expression.Lambda<Func<obj, Delegate>>(Expression.Convert(honest, typeof<Delegate>), "dlr-functionToDelegate", [ fParam ]).Compile())
                | None -> None
            conversions.[struct (funcType, delegateType)] <- conversion
            conversion

    /// The delegate over `f`.
    let Make (delegateType: Type) (f: obj) : Delegate =
        match tryConversion (f.GetType()) delegateType with
        | Some factory -> factory.Invoke f
        | None -> raise (RuntimeBinderException(sprintf "Cannot convert an F# function of type '%s' to '%s'" (f.GetType().Name) delegateType.Name))

    /// `Make` as a method, for the expression tree of a bound call to name it.
    let makeMethod : MethodInfo = Quotation.methodOf <@ Make typeof<obj> null @>

/// What a member read as a delegate type yields when the member is a method
/// (`FSharpGetMemberOrMethodBinder`): the block then makes the delegate an invoker of it.
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
[<Sealed>]
type MethodGroup private () =
    static let instance = MethodGroup()
    static member Instance : obj = box instance
    static member Is(value: obj) = obj.ReferenceEquals(value, instance)

/// A member read as an F# function type: `let f: int -> int -> int = dlr { return x?Add }`. The
/// value is an F# function (curried, so partial application works) that invokes the member with
/// the collected arguments when fully applied and converts the result; the site's binder handles
/// methods, delegates and F# function values. Tupled variants for `A * B -> R`.
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
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
