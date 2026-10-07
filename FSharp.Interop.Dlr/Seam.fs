namespace FSharp.Interop.Dlr

open System
open System.Dynamic
open System.Linq.Expressions
open System.Reflection
open System.Runtime.CompilerServices
open Microsoft.CSharp.RuntimeBinder
open FSharp.Quotations

/// The reflection fallback: the rules the binders offer C# as its error suggestion (or, where C#
/// would bind wrongly or crash, ahead of it — `Seam`). Calls (`tryCall`, `tryStaticCall`),
/// constructors (`tryConstruct`), a delegate target's `Invoke` (`tryInvokeDelegate`) and
/// assignments (`trySet`), each choosing among candidates by one rule (`tryInvoke`); every argument
/// meets its slot through `convertValue`. F# optional parameters (`?arg`) compile to
/// `FSharpOption<'T>` parameters carrying `[<OptionalArgument>]`, which C#'s binder neither omits
/// nor fills: here omitted ones are `None` and a bare value is wrapped in `Some`.
module internal Fallback =
    let private isOptional (p: ParameterInfo) =
        p.GetCustomAttributes(typeof<OptionalArgumentAttribute>, false).Length > 0
        && p.ParameterType.IsGenericType
        && p.ParameterType.GetGenericTypeDefinition() = typedefof<option<_>>

    let private isNullValue = FunctionShapes.isNullValue

    /// A concrete delegate type: `Delegate` and `MulticastDelegate` themselves have no `Invoke`.
    let private isDelegate (t: Type) = typeof<Delegate>.IsAssignableFrom t && not (isNull (DelegateMembers.invokeOf t))
    let private isAbstractDelegate (t: Type) = t = typeof<Delegate> || t = typeof<MulticastDelegate>

    /// An F# function value for a delegate-typed parameter: a delegate over the function
    /// (`FunctionAdapters`), as F# itself converts a lambda argument to a `Func`/`Action`
    /// parameter at a static call.
    let private functionToDelegate (delegateType: Type) (a: DynamicMetaObject) : Expression option =
        match FunctionConversions.tryConversion a.LimitType delegateType with
        | None -> None
        | Some _ ->
            let make = FunctionConversions.makeMethod
            Some(Expression.Convert(Expression.Call(make, Expression.Constant delegateType, Expression.Convert(a.Expression, typeof<obj>)), delegateType) :> Expression)

    /// A delegate for an F# function-typed parameter: `DelegateFunction`'s typed wrapper, offered
    /// exactly when `DelegateConversions.tryTyped` has one for this pair (`Signatures`).
    let private delegateToFunction (funcType: Type) (a: DynamicMetaObject) : Expression option =
        match DelegateConversions.tryTyped funcType a.LimitType with
        | Some _ ->
            let make = DelegateFunction.makeMethod
            Some(Expression.Convert(Expression.Call(make, Expression.Constant funcType, Expression.Convert(a.Expression, typeof<Delegate>)), funcType) :> Expression)
        | None -> None

    /// An F# function for a slot typed `Delegate` itself (Control.Invoke): the Func/Action F#
    /// would build for the function. Left to C#, FSharpFunc's own op_Implicit makes a
    /// Converter<Unit, R> of a `unit -> R` — a one-parameter delegate, wrong for a
    /// `DynamicInvoke()` — so this goes first wherever such a slot is the target.
    let private toAbstractDelegate (a: DynamicMetaObject) : Expression option =
        match FunctionShapes.parameters a.LimitType with
        | Some(ds, _, _) when not (DelegateMembers.funcFits ds.Length) -> None     // no Func/Action of that many parameters
        | Some(ds, _, result) ->
            let delegateType = if result = typeof<unit> then Expression.GetActionType(Array.ofList ds) else Expression.GetFuncType(Array.ofList (ds @ [ result ]))
            functionToDelegate delegateType a
        | None -> None

    /// The conversions an argument can need for a typed slot that neither assignment nor widening
    /// gives: an F# function for a delegate slot, a delegate for a function slot (both by the one
    /// rule, `Signatures`), an F# function for a slot typed `Delegate` itself.
    let conversion (slot: Type) (a: DynamicMetaObject) : Expression option =
        let at = a.LimitType
        if isDelegate slot && (FunctionShapes.domains at).IsSome then functionToDelegate slot a
        elif isDelegate at && (FunctionShapes.domains slot).IsSome then delegateToFunction slot a
        elif isAbstractDelegate slot && (FunctionShapes.domains at).IsSome then toAbstractDelegate a
        else None

    /// The one place a dynamic argument meets a typed slot: the argument as a `slot`, or None if it
    /// does not fit — assignable or C#-widened (unboxed at its runtime type first: converting `obj`
    /// straight to `int64` would unbox a boxed `int` as `int64` and throw), a null for a reference
    /// or nullable slot, or one of the `conversion`s.
    let convertValue (slot: Type) (a: DynamicMetaObject) : Expression option =
        if isNullValue a then
            if slot.IsValueType && isNull (Nullable.GetUnderlyingType slot) then None
            else Some(Expression.Constant(null, slot) :> Expression)
        elif Conversions.fits slot a.LimitType then Some(Expression.Convert(Expression.Convert(a.Expression, a.LimitType), slot) :> Expression)
        else conversion slot a

    /// An argument for a method parameter: `convertValue`, or a bare value for an F# optional
    /// parameter (`?x`, an `FSharpOption`) as `Some`.
    let private fit (p: ParameterInfo) (a: DynamicMetaObject) : Expression option =
        let pt = p.ParameterType
        match convertValue pt a with
        | Some e -> Some e
        | None when isOptional p && Conversions.fits (pt.GetGenericArguments().[0]) a.LimitType ->
            let inner = pt.GetGenericArguments().[0]
            Some(Expression.Call(pt.GetMethod("Some"), Expression.Convert(Expression.Convert(a.Expression, a.LimitType), inner)) :> Expression)
        | None -> None

    /// A call has an argument that is an F# function in a slot typed `Delegate` in some candidate
    /// method: C# would bind it through op_Implicit to a `Converter`, wrongly, so our rule goes first.
    let hasAbstractDelegateSlot (context: Type) (t: Type) (name: string) (args: DynamicMetaObject[]) =
        t.GetMethods(Accessibility.all)
        |> Array.exists (fun m ->
            m.Name = name && Accessibility.method' context t m
            && (let ps = m.GetParameters()
                ps.Length >= args.Length
                && Array.exists2 (fun (p: ParameterInfo) (a: DynamicMetaObject) -> isAbstractDelegate p.ParameterType && (FunctionShapes.domains a.LimitType).IsSome) (Array.sub ps 0 args.Length) args))

    // `FunctionShapes.applyCall` (defined before the conversions, which use it for the largest
    // delegates) takes them through this hook for an F# function's domains.
    do FunctionShapes.convertArgument <- conversion

    /// Not C#'s overload resolution, but deterministic: among the candidates the arguments fit,
    /// the one with the most exactly-typed argument slots wins, then the one with the fewest
    /// omitted parameters; a tie is ambiguous and left to C#'s error. `instance` is the receiver
    /// for instance methods, None for static methods and constructors; `call` builds the
    /// invocation of the chosen candidate.
    /// A delegate argument for a function slot, or a function for a delegate slot, of exactly the
    /// slot's signature (no variance): counted as an exact match when ranking candidates.
    let private exactConversion (slot: Type) (argType: Type) =
        let signature (funcType: Type) (delegateType: Type) =
            match FunctionShapes.parameters funcType, DelegateMembers.invokeOf delegateType with
            | Some(ds, _, result), invoke when not (isNull invoke) ->
                Signatures.exactly ds result [ for p in invoke.GetParameters() -> p.ParameterType ] invoke.ReturnType
            | _ -> false
        if isDelegate argType && (FunctionShapes.domains slot).IsSome then signature slot argType
        elif isDelegate slot && (FunctionShapes.domains argType).IsSome then signature argType slot
        else false

    let private tryInvoke (candidates: MethodBase[]) (call: MethodBase -> Expression list -> Expression) (targetRestriction: BindingRestrictions) (args: DynamicMetaObject[]) : DynamicMetaObject option =
        let fitting =
            candidates
            |> Array.choose (fun m ->
                let ps = m.GetParameters()
                let required = ps |> Array.filter (fun p -> not (isOptional p)) |> Array.length
                if args.Length < required || args.Length > ps.Length then None
                else
                    let supplied = [ for i in 0 .. args.Length - 1 -> fit ps.[i] args.[i] ]
                    if supplied |> List.exists Option.isNone then None
                    else
                        let exact = Seq.zip ps args |> Seq.filter (fun (p, a) -> p.ParameterType = a.LimitType || exactConversion p.ParameterType a.LimitType) |> Seq.length
                        Some(m, ps, List.choose id supplied, exact))
            |> Array.sortByDescending (fun (_, ps, _, exact) -> exact, -ps.Length)
        match List.ofArray fitting with
        | (_, ps1, _, e1) :: (_, ps2, _, e2) :: _ when e1 = e2 && ps1.Length = ps2.Length -> None   // ambiguous
        | (m, ps, supplied, _) :: _ ->
            let omitted = [ for i in args.Length .. ps.Length - 1 -> Expression.Constant(null, ps.[i].ParameterType) :> Expression ]
            let invocation = call m (supplied @ omitted)
            let value =
                if invocation.Type = typeof<Void> then Expression.Block(invocation, Expression.Constant(null, typeof<obj>)) :> Expression
                else Expression.Convert(invocation, typeof<obj>) :> Expression
            let restrictions = Array.fold (fun (r: BindingRestrictions) a -> r.Merge(FunctionShapes.restrictArg a)) targetRestriction args
            Some(DynamicMetaObject(value, restrictions))
        | [] -> None

    /// The target as a `t` to call or assign through: a struct in its box (`Unbox`), as C# does, so
    /// a mutating method or an assignment changes the boxed value — `Convert` would unbox a copy.
    let private receiver (t: Type) (target: DynamicMetaObject) : Expression =
        if t.IsValueType then Expression.Unbox(target.Expression, t) :> Expression else Expression.Convert(target.Expression, t) :> Expression

    /// An instance method of `t` named `name` the arguments fit.
    let tryCall (context: Type) (t: Type) (name: string) (target: DynamicMetaObject) (args: DynamicMetaObject[]) : DynamicMetaObject option =
        let candidates =
            t.GetMethods(Accessibility.all)
            |> Array.filter (fun m -> m.Name = name && not m.IsGenericMethodDefinition && Accessibility.method' context t m)
            |> Array.map (fun m -> m :> MethodBase)
        let self = receiver t target
        tryInvoke candidates (fun m ps -> Expression.Call(self, m :?> MethodInfo, ps) :> Expression) (BindingRestrictions.GetTypeRestriction(target.Expression, t)) args

    /// The index types C# takes for an array: int, uint, long, ulong, and those that widen to them.
    let private arrayIndexTypes =
        [| typeof<int>; typeof<uint32>; typeof<int64>; typeof<uint64>; typeof<sbyte>; typeof<byte>; typeof<int16>; typeof<uint16>; typeof<char> |]

    /// The settable slots of `t` an assignment names: the property or field `name`, or with
    /// `indexes` the indexer (the `DefaultMember`) of that many indexes, or an array's element.
    let private slotTypes (t: Type) (name: string option) (indexCount: int) : Type list =
        let indexerName = match t.GetCustomAttributes(typeof<DefaultMemberAttribute>, true) with [| :? DefaultMemberAttribute as d |] -> d.MemberName | _ -> "Item"
        [ for p in t.GetProperties(Accessibility.all) do
            if p.Name = defaultArg name indexerName && p.GetIndexParameters().Length = indexCount && p.CanWrite then yield p.PropertyType
          match name with
          | Some n when indexCount = 0 ->
              for f in t.GetFields(Accessibility.all) do if f.Name = n && not f.IsInitOnly && not f.IsLiteral then yield f.FieldType
          | None when t.IsArray && t.GetArrayRank() = indexCount -> yield t.GetElementType()
          | _ -> () ]

    /// An F# function assigned to a slot typed `Delegate` itself: C# would bind it, wrongly, through
    /// FSharpFunc's op_Implicit (see `toAbstractDelegate`), so `trySet` goes first there.
    let assignsAbstractDelegate (t: Type) (name: string option) (indexCount: int) (value: DynamicMetaObject) =
        (FunctionShapes.domains value.LimitType).IsSome && slotTypes t name indexCount |> List.exists isAbstractDelegate

    /// An assignment C# could not bind because the value needs one of the conversions above (an F#
    /// function for a delegate-typed slot, a delegate for a function-typed one; #153): a property's
    /// setter, or with `indexes` an indexer's, called with the indexes then the value through
    /// `tryInvoke`; a field or an array element assigned the converted value. The result is the
    /// value as assigned, boxed, as C#'s assignment's is. None when nothing of that name fits.
    let trySet (context: Type) (t: Type) (name: string option) (target: DynamicMetaObject) (indexes: DynamicMetaObject[]) (value: DynamicMetaObject) : DynamicMetaObject option =
        let self = receiver t target
        let targetRestriction = BindingRestrictions.GetTypeRestriction(target.Expression, t)
        let convertSlot (slotType: Type) = conversion slotType value
        let restrictions () = Array.fold (fun (r: BindingRestrictions) a -> r.Merge(FunctionShapes.restrictArg a)) targetRestriction (Array.append indexes [| value |])
        let assigned (slot: Expression) (converted: Expression) =
            let v = Expression.Variable(slot.Type, "value")
            Expression.Block([ v ], Expression.Assign(v, converted), Expression.Assign(slot, v), Expression.Convert(v, typeof<obj>)) :> Expression
        let indexerName = match t.GetCustomAttributes(typeof<DefaultMemberAttribute>, true) with [| :? DefaultMemberAttribute as d |] -> d.MemberName | _ -> "Item"
        let setters =
            t.GetProperties(Accessibility.all)
            |> Array.filter (fun p ->
                p.Name = defaultArg name indexerName && p.GetIndexParameters().Length = indexes.Length
                && (let m = p.GetSetMethod(true) in not (isNull m) && Accessibility.method' context t m))
            |> Array.map (fun p -> p.GetSetMethod(true) :> MethodBase)
        if setters.Length > 0 then
            let call (m: MethodBase) (ps: Expression list) =
                let v = Expression.Variable((List.last ps).Type, "value")
                Expression.Block([ v ], Expression.Assign(v, List.last ps),
                                 Expression.Call(self, m :?> MethodInfo, List.take (ps.Length - 1) ps @ [ v :> Expression ]),
                                 v) :> Expression
            tryInvoke setters call targetRestriction (Array.append indexes [| value |])
        else
            match name with
            | Some name when indexes.Length = 0 ->
                t.GetFields(Accessibility.all)
                |> Array.tryFind (fun f -> f.Name = name && not f.IsInitOnly && not f.IsLiteral && Accessibility.field context t f)
                |> Option.bind (fun f ->
                    convertSlot f.FieldType
                    |> Option.map (fun converted -> DynamicMetaObject(assigned (Expression.Field(self, f)) converted, restrictions ())))
            | None when t.IsArray && t.GetArrayRank() = indexes.Length && indexes |> Array.forall (fun i -> arrayIndexTypes |> Array.contains i.LimitType) ->
                convertSlot (t.GetElementType())
                |> Option.map (fun converted ->
                    // Unboxed at its runtime type, then checked to int, as C#'s index would overflow.
                    let element = Expression.ArrayAccess(self, [ for i in indexes -> Expression.ConvertChecked(Expression.Convert(i.Expression, i.LimitType), typeof<int>) :> Expression ])
                    DynamicMetaObject(assigned element converted, restrictions ()))
            | _ -> None

    /// A static method of `t` named `name` the arguments fit (`Dlr.Static<T>.Overloads`).
    let tryStaticCall (context: Type) (t: Type) (name: string) (target: DynamicMetaObject) (args: DynamicMetaObject[]) : DynamicMetaObject option =
        let candidates =
            t.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.FlattenHierarchy)
            |> Array.filter (fun m -> m.Name = name && not m.IsGenericMethodDefinition && Accessibility.method' context null m)
            |> Array.map (fun m -> m :> MethodBase)
        tryInvoke candidates (fun m ps -> Expression.Call(m :?> MethodInfo, ps) :> Expression) (BindingRestrictions.GetInstanceRestriction(target.Expression, target.Value)) args

    /// A constructor of `t` the arguments fit (`Dlr.new'<T>`).
    let tryConstruct (context: Type) (t: Type) (target: DynamicMetaObject) (args: DynamicMetaObject[]) : DynamicMetaObject option =
        let candidates =
            t.GetConstructors(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance)
            |> Array.filter (fun c -> Accessibility.method' context null c)
            |> Array.map (fun c -> c :> MethodBase)
        tryInvoke candidates (fun c ps -> Expression.New(c :?> ConstructorInfo, ps) :> Expression) (BindingRestrictions.GetInstanceRestriction(target.Expression, target.Value)) args

    /// A delegate target's `Invoke` the arguments fit (`Dlr.call` on a delegate, a delegate-typed
    /// member, an Expando's delegate member): the conversions above apply to its parameters.
    let tryInvokeDelegate (target: DynamicMetaObject) (args: DynamicMetaObject[]) : DynamicMetaObject option =
        let dt = target.LimitType
        if not (typeof<Delegate>.IsAssignableFrom dt) || isNull (DelegateMembers.invokeOf dt) then None
        else
            let invoke = DelegateMembers.invokeOf dt
            let self = Expression.Convert(target.Expression, dt)
            tryInvoke [| invoke |] (fun m ps -> Expression.Call(self, m :?> MethodInfo, ps) :> Expression) (BindingRestrictions.GetTypeRestriction(target.Expression, dt)) args

/// Where our rule stands relative to C#'s (the seam, `docs/binders.md`): C# binds first and ours
/// is its error suggestion, used only where C# fails — except where C# would bind *wrongly* or
/// crash, where ours goes first. Each binder states which case it is in through this one call.
module internal Seam =
    /// Our rule first when `csharpWouldBeWrong` and there is one; else C#'s binding (`csharp`, given
    /// the error suggestion), with ours, if any, as that suggestion.
    let oursFirstWhen (csharpWouldBeWrong: bool) (ours: DynamicMetaObject option) (errorSuggestion: DynamicMetaObject)
                      (csharp: DynamicMetaObject -> DynamicMetaObject) : DynamicMetaObject =
        match ours with
        | Some rule when csharpWouldBeWrong -> rule
        | _ -> csharp (defaultArg ours errorSuggestion)

/// Equality and ordering with F# semantics where C# has none: records, unions, tuples, lists,
/// options, sets and any other type without the CLR operator get `=`/`compare` (structural,
/// through `LanguagePrimitives`) instead of C#'s reference equality or "operator cannot be
/// applied". Types C# handles itself — primitives, enums, strings, delegates, and any type that
/// declares the operator — keep C#'s binding, as do all non-comparison operators.
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpBinaryOperationBinder(csharp: BinaryOperationBinder) =
    inherit BinaryOperationBinder(csharp.Operation)

    static let operatorName =
        dict [ ExpressionType.Equal, "op_Equality"; ExpressionType.NotEqual, "op_Inequality"
               ExpressionType.LessThan, "op_LessThan"; ExpressionType.LessThanOrEqual, "op_LessThanOrEqual"
               ExpressionType.GreaterThan, "op_GreaterThan"; ExpressionType.GreaterThanOrEqual, "op_GreaterThanOrEqual" ]

    static let equality = Quotation.methodOf <@ LanguagePrimitives.GenericEquality (box 1) (box 2) @>

    static let comparison =
        (Quotation.methodOf <@ LanguagePrimitives.GenericComparison (box 1 :?> IComparable) (box 2 :?> IComparable) @>)
            .GetGenericMethodDefinition().MakeGenericMethod typeof<obj>

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

    /// Whether the operation is one this binder may bind structurally.
    static member IsComparison(op: ExpressionType) = operatorName.ContainsKey op

    override this.FallbackBinaryOperation(target, arg, errorSuggestion) =
        let structural =
            match operatorName.TryGetValue this.Operation with
            | true, name ->
                let operands = [ target; arg ] |> List.filter (fun a -> not (FunctionShapes.isNullValue a)) |> List.map (fun a -> a.LimitType)
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
            DynamicMetaObject(Expression.Convert(value, typeof<obj>), (FunctionShapes.restrictArg target).Merge(FunctionShapes.restrictArg arg))

/// A seam rule for meta-object targets: an F# function argument (or value) handed to an
/// `IDynamicMetaObjectProvider` — a script object, a `DynamicObject` someone wrote — becomes the
/// delegate of its own signature before the meta-object sees it. Every meta-object understands
/// delegates and none an `FSharpFunc`; for a CLR target the parameter type drives the conversion
/// instead, so nothing C# binds changes. Restricted on the function type, so one rule serves every
/// lambda of that signature.
module internal MetaObjectArguments =
    let private isMetaObject (target: DynamicMetaObject) =
        target.HasValue && (target.Value :? IDynamicMetaObjectProvider)

    /// The delegate type an F# function of `funcType` is naturally: `Func<…>`, `Action<…>` for a
    /// unit result; a tupled function by its elements.
    let private delegateTypeOf (funcType: Type) =
        match FunctionShapes.parameters funcType with
        | Some(ds, _, result) ->
            let result = if result = typeof<unit> then typeof<Void> else result
            Some(Expression.GetDelegateType(Array.ofList (ds @ [ result ])))
        | None -> None

    /// `a` as the delegate of its signature, if it is an F# function value; else `a` restricted on
    /// its type (or on null), so the rule is per argument-type combination and a later function in
    /// this slot binds anew rather than reaching the meta-object raw.
    let private asDelegate (a: DynamicMetaObject) =
        let asIs () = DynamicMetaObject(a.Expression, FunctionShapes.restrictArg a, (if a.HasValue then a.Value else null))
        if not a.HasValue || isNull a.Value then asIs ()
        else
        match FunctionShapes.funcBase a.LimitType with
        | None -> asIs ()
        | Some funcType ->
            match delegateTypeOf funcType |> Option.bind (fun dt -> FunctionConversions.tryConversion funcType dt |> Option.map (fun f -> dt, f)) with
            | None -> asIs ()
            | Some(delegateType, factory) ->
                let expr = Expression.Convert(Expression.Invoke(Expression.Constant factory, Expression.Convert(a.Expression, typeof<obj>)), delegateType)
                let restriction = BindingRestrictions.GetExpressionRestriction(Expression.TypeIs(a.Expression, funcType))
                DynamicMetaObject(expr, restriction, factory.Invoke a.Value)

    /// Whether the rule applies: a meta-object target with an F# function among the arguments.
    let applies (target: DynamicMetaObject) (args: DynamicMetaObject[]) =
        isMetaObject target && args |> Array.exists (fun a -> a.HasValue && not (isNull a.Value) && (FunctionShapes.funcBase a.LimitType).IsSome)

    /// The rule: a nested site on `inner` whose arguments are the delegates — a meta-object (a
    /// `DynamicObject` above all) wants each argument to be the site's own parameter, which the
    /// nested site's are — restricted on the target's type and each function's type.
    let rule (inner: DynamicMetaObjectBinder) (target: DynamicMetaObject) (args: DynamicMetaObject[]) =
        let converted = Array.map asDelegate args
        let restrictions =
            converted
            |> Array.fold (fun (r: BindingRestrictions) a -> r.Merge a.Restrictions)
                (BindingRestrictions.GetTypeRestriction(target.Expression, target.LimitType))
        let call = Expression.Dynamic(inner, typeof<obj>, Array.append [| target.Expression |] [| for a in converted -> a.Expression |])
        DynamicMetaObject(call, restrictions)

/// The standard binders seal `Bind`, so the rule sits one level out: this binder answers a
/// meta-object target with an F# function argument by the nested-site rule, and hands every
/// other bind to the real binder, which the meta-object then sees as usual.
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type MetaObjectAwareBinder(inner: DynamicMetaObjectBinder) =
    inherit DynamicMetaObjectBinder()
    member _.Inner = inner
    override _.Bind(target: DynamicMetaObject, args: DynamicMetaObject[]) =
        if MetaObjectArguments.applies target args then MetaObjectArguments.rule inner target args
        else inner.Bind(target, args)

/// C#'s Invoke binder, aware of F# function targets (`Dlr.call` on a function value, and the
/// value step of a member invocation).
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
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
            | None ->
                // A delegate target: C# invokes it, and our rule for F# function / delegate /
                // optional-parameter arguments is its error suggestion — or goes first where C#
                // would crash rather than bind (an internal delegate on .NET Framework).
                Seam.oursFirstWhen (DelegateMembers.csharpCannotInvoke target.LimitType) (Fallback.tryInvokeDelegate target args) errorSuggestion
                    (fun suggestion -> csharp.FallbackInvoke(target, args, suggestion))

/// C#'s Invoke over byref parameters (`Dlr.call f (…, Dlr.out)`, `Dlr.apply`), with our rule — the
/// delegate's own `Invoke` called directly, the byref arguments passed as the site's own byref
/// parameters so LINQ writes them back, the others fitted by `convertValue` (so an F# function
/// for a delegate parameter converts, as in a call without byrefs) — as C#'s error suggestion.
/// It goes first only where C# would crash: on .NET Framework, invoking an F# `internal` delegate
/// (its `Expression.Invoke` looks `Invoke` up public-only; `DelegateMembers.csharpCannotInvoke`).
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpByRefInvokeBinder(csharp: InvokeBinder, discard: bool, flags: CSharpArgumentInfoFlags[]) =
    inherit InvokeBinder(csharp.CallInfo)

    override _.FallbackInvoke(target, args, errorSuggestion) =
        let direct =
            let dt = target.LimitType
            let invoke = if target.HasValue && not (isNull target.Value) && typeof<Delegate>.IsAssignableFrom dt then DelegateMembers.invokeOf dt else null
            if isNull invoke then None
            else
                let ps = invoke.GetParameters()
                // Which parameter each argument is, as C# matches them: the positional ones first,
                // then each named one by its name (`Dlr.named`). None for an unknown or repeated
                // name, or a parameter left without an argument.
                let names = List.ofSeq csharp.CallInfo.ArgumentNames
                let positional = args.Length - names.Length
                let slots =
                    [ for i in 0 .. positional - 1 -> Some i ] @ [ for n in names -> ps |> Array.tryFindIndex (fun p -> p.Name = n) ]
                let order = if slots |> List.exists Option.isNone then None else Some(List.map Option.get slots)
                match order with
                | Some order when ps.Length = args.Length && List.length (List.distinct order) = order.Length
                                  // A void delegate's result is C#'s error unless the site discards it.
                                  && (discard || invoke.ReturnType <> typeof<Void>) ->
                    let passed =
                        List.zip order (List.ofArray args)
                        |> List.mapi (fun i (slot, a) ->
                            let p = ps.[slot]
                            let pt = p.ParameterType
                            let isOut = flags.[i].HasFlag CSharpArgumentInfoFlags.IsOut
                            let isRef = flags.[i].HasFlag CSharpArgumentInfoFlags.IsRef
                            let expr =
                                if pt.IsByRef then
                                    // The site's byref parameter itself, `out` exactly for an out
                                    // parameter as C# requires: anything else is C#'s to refuse.
                                    match a.Expression with
                                    | :? ParameterExpression as v when v.IsByRef && v.Type = pt.GetElementType() && (isOut || isRef) && isOut = p.IsOut -> Some(v :> Expression)
                                    | _ -> None
                                elif isOut || isRef then None                  // a ref or out for a plain parameter
                                else Fallback.convertValue pt a
                            slot, expr)
                    if passed |> List.exists (snd >> Option.isNone) then None
                    else
                        let byPosition = passed |> List.sortBy fst |> List.map (snd >> Option.get)
                        let call = Expression.Call(Expression.Convert(target.Expression, dt), invoke, byPosition)
                        let value =
                            if invoke.ReturnType = typeof<Void> then Expression.Block(call, Expression.Constant(null, typeof<obj>)) :> Expression
                            else Expression.Convert(call, typeof<obj>) :> Expression
                        let restrictions =
                            List.zip order (List.ofArray args)
                            |> List.fold (fun (r: BindingRestrictions) (slot, a) -> if ps.[slot].ParameterType.IsByRef then r else r.Merge(FunctionShapes.restrictArg a))
                                (BindingRestrictions.GetTypeRestriction(target.Expression, dt))
                        Some(DynamicMetaObject(value, restrictions))
                | _ -> None
        Seam.oursFirstWhen (DelegateMembers.csharpCannotInvoke target.LimitType) direct errorSuggestion
            (fun suggestion -> csharp.FallbackInvoke(target, args, suggestion))

/// C#'s InvokeMember binder, aware of F# function values: when C# cannot invoke a member because
/// it holds an `FSharpFunc` rather than a delegate, the rule applies the function instead. The
/// decision is a binding rule restricted to the runtime type, so a site that sees several kinds of
/// target keeps one cached rule per kind, as the DLR intends: no exceptions, no per-site state.
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
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
        let restrictions = FunctionShapes.restrictions target t args
        let direct =
            FunctionShapes.clrMember context t name target
            |> Option.bind (fun (mt, read) ->
                match FunctionShapes.applyCall mt read args with
                | Some call -> Some(DynamicMetaObject(call, restrictions))
                | None when FunctionShapes.opaque mt || typeof<Delegate>.IsAssignableFrom mt ->
                    // Opaque (`obj`, an interface): decide by the value's runtime type. A delegate
                    // member: invoke it through a site whose binder converts F# function arguments.
                    let nested = Expression.Dynamic(invoke, typeof<obj>, (read :: [ for a in args -> a.Expression ]))
                    Some(DynamicMetaObject(nested, restrictions))
                | None -> None)
        let hasMethod =
            t.GetMethods(Accessibility.all) |> Array.exists (fun m -> m.Name = name && Accessibility.method' context t m)
        let allValues = target.HasValue && (args |> Array.forall (fun a -> a.HasValue))
        // A function argument for a `Delegate`-typed parameter: C# would bind FSharpFunc's
        // op_Implicit Converter, wrongly, so our call goes first there.
        let abstractDelegateCall =
            if allValues && Fallback.hasAbstractDelegateSlot context t name args then Fallback.tryCall context t name target args
            else None
        match direct, abstractDelegateCall with
        | Some rule, _ when not hasMethod -> rule
        | _, Some rule -> rule
        | _ ->
            // A method of that name exists: C# binds it; our rules (a function-valued member of
            // the same name, or F# optional parameters) are only its error suggestion.
            let suggestion =
                match direct with
                | Some rule -> rule
                | None ->
                    if allValues then
                        match Fallback.tryCall context t name target args with
                        | Some rule -> rule
                        | None -> errorSuggestion
                    else errorSuggestion
            csharp.FallbackInvokeMember(target, args, suggestion)

    /// A dynamic target (Expando, DynamicObject) produced the member's value and asks for it to be
    /// invoked: an F# function is applied, anything else is C#'s Invoke (see FSharpInvokeBinder).
    override _.FallbackInvoke(target, args, errorSuggestion) = invoke.FallbackInvoke(target, args, errorSuggestion)

/// C#'s InvokeMember binder for a static target (`Dlr.Static<T>.Overloads?M(…)`): C# binds, and
/// our rule for F# optional parameters and function/delegate arguments on the static methods of
/// `T` is its error suggestion — the same rules an instance call gets.
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpStaticInvokeMemberBinder(context: Type, name: string, csharp: InvokeMemberBinder) =
    inherit InvokeMemberBinder(name, false, csharp.CallInfo)

    override _.FallbackInvokeMember(target, args, errorSuggestion) =
        let suggestion =
            if target.HasValue && (args |> Array.forall (fun a -> a.HasValue)) then
                match Fallback.tryStaticCall context (target.Value :?> Type) name target args with
                | Some rule -> rule
                | None -> errorSuggestion
            else errorSuggestion
        csharp.FallbackInvokeMember(target, args, suggestion)

    override _.FallbackInvoke(target, args, errorSuggestion) = csharp.FallbackInvoke(target, args, errorSuggestion)

/// C#'s InvokeConstructor binder (`Dlr.new'<T>`) with our rule for F# optional parameters and
/// function/delegate arguments. C#'s constructor binder takes no error suggestion — its failure
/// is a rule that throws — so ours applies when C#'s bind is that throw, and only then.
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpInvokeConstructorBinder(context: Type, t: Type, csharp: DynamicMetaObjectBinder) =
    inherit DynamicMetaObjectBinder()

    static let rec isThrow (e: Expression) =
        match e.NodeType with
        | ExpressionType.Throw -> true
        | ExpressionType.Convert | ExpressionType.ConvertChecked -> isThrow (e :?> UnaryExpression).Operand
        | ExpressionType.Block -> isThrow (e :?> BlockExpression).Result
        | _ -> false

    override _.ReturnType = t

    override _.Bind(target, args) =
        let csharpRule = csharp.Bind(target, args)
        if isThrow csharpRule.Expression && target.HasValue && (args |> Array.forall (fun a -> a.HasValue)) then
            match Fallback.tryConstruct context t target args with
            | Some rule -> DynamicMetaObject(Expression.Convert(rule.Expression, t), rule.Restrictions)
            | None -> csharpRule
        else csharpRule

/// C#'s SetMember, with an F# function value handed to a meta-object as the delegate of its
/// signature (`w?onClick <- fun () -> …` on a script object, through MetaObjectAwareBinder); on a
/// CLR target, our conversion of the value to the slot's type (an F# function to a delegate-typed
/// property or field, a delegate to a function-typed one) as C#'s error suggestion (#153).
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpSetMemberBinder(context: Type, name: string, csharp: SetMemberBinder) =
    inherit SetMemberBinder(name, false)
    override _.FallbackSetMember(target, value, errorSuggestion) =
        let ours =
            if target.HasValue && value.HasValue && not (isNull target.Value) then
                Fallback.trySet context target.LimitType (Some name) target [||] value
            else None
        // An F# function into a `Delegate`-typed slot: C# would bind FSharpFunc's op_Implicit Converter.
        Seam.oursFirstWhen (Fallback.assignsAbstractDelegate target.LimitType (Some name) 0 value) ours errorSuggestion
            (fun suggestion -> csharp.FallbackSetMember(target, value, suggestion))

/// C#'s SetIndex, with the same conversion of the value as C#'s error suggestion: an F# function
/// into a delegate-typed indexer slot or array element (a `Dictionary<string, Func<…>>`), a
/// delegate into a function-typed one (#153).
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpSetIndexBinder(context: Type, csharp: SetIndexBinder) =
    inherit SetIndexBinder(csharp.CallInfo)
    override this.FallbackSetIndex(target, indexes, value, errorSuggestion) =
        if not target.HasValue || not value.HasValue || indexes |> Array.exists (fun i -> not i.HasValue) then
            this.Defer(Array.concat [ [| target |]; indexes; [| value |] ])
        else
            let ours = if isNull target.Value then None else Fallback.trySet context target.LimitType None target indexes value
            Seam.oursFirstWhen (Fallback.assignsAbstractDelegate target.LimitType None indexes.Length value) ours errorSuggestion
                (fun suggestion -> csharp.FallbackSetIndex(target, indexes, value, suggestion))

/// The value of a member read as `unit -> R`: an F# function is applied, a delegate invoked, any
/// other value is the result itself.
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
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
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
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
                    match Fallback.tryCall context t name target args with
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

/// A member read as a delegate type (`Api.Fold(dlr { return x?Add })`, #201): C#'s GetMember, with
/// our rule as its error suggestion where C# fails because the name is a method: the
/// `MethodGroup` marker, which the block answers with an invoker of the method as the delegate.
/// A property, field or dynamic object's member keeps its own rule (a meta-object's, then C#'s),
/// so that read costs what it did.
/// Not part of the supported API: public only because compiled blocks call it, and it may change in any release.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type FSharpGetMemberOrMethodBinder(context: Type, name: string, csharp: GetMemberBinder) =
    inherit GetMemberBinder(name, false)
    override _.FallbackGetMember(target, errorSuggestion) =
        let ours =
            if target.HasValue && not (isNull target.Value) then
                let t = target.LimitType
                let isMethod =
                    t.GetMethods(Accessibility.all)
                    |> Array.exists (fun m -> m.Name = name && not m.IsStatic && not m.IsSpecialName && Accessibility.method' context t m)
                if isMethod then
                    Some(DynamicMetaObject(Expression.Constant(MethodGroup.Instance, typeof<obj>), BindingRestrictions.GetTypeRestriction(target.Expression, t)))
                else None
            else None
        // Ours only where nothing else is suggested: a meta-object's own rule (a DynamicObject's
        // `TryGetMember ? value : fallback`) arrives as the suggestion and comes first, as in C#;
        // its first probe, with none, gets ours as the inner fallback.
        csharp.FallbackGetMember(target, (if isNull errorSuggestion then defaultArg ours null else errorSuggestion))
