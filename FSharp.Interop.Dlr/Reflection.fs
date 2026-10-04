namespace FSharp.Interop.Dlr

open System
open System.Diagnostics.CodeAnalysis
open System.Dynamic
open System.Linq.Expressions
open System.Reflection
open System.Runtime.CompilerServices
open Microsoft.CSharp.RuntimeBinder
open FSharp.Quotations

/// The accessibility rule the C# binder applies from its context type — public; internal from the
/// assembly or one it names in [<InternalsVisibleTo>] (F# `private` compiles to internal);
/// protected from a derived type, through a receiver of that type; private from inside the
/// declaring type — for a member and for its declaring type at every nesting level (a constructed
/// generic is as visible as its arguments). Our own reflection lookups apply the same rule.
module internal Accessibility =
    /// Whether `declaring`'s assembly opens its internals to `context`'s: the same one, or one
    /// it names in an [<InternalsVisibleTo>] (the C# binder honours that too). Per pair, cached.
    let private opensTo = System.Collections.Concurrent.ConcurrentDictionary<struct (Reflection.Assembly * Reflection.Assembly), bool>()
    let private sameAssembly (context: Type) (declaring: Type) =
        let c, d = context.Assembly, declaring.Assembly
        c = d
        || opensTo.GetOrAdd(struct (d, c), fun _ ->
            let name = c.GetName().Name
            d.GetCustomAttributes(typeof<System.Runtime.CompilerServices.InternalsVisibleToAttribute>, false)
            |> Seq.cast<System.Runtime.CompilerServices.InternalsVisibleToAttribute>
            |> Seq.exists (fun a -> let n = a.AssemblyName in (match n.IndexOf ',' with -1 -> n | i -> n.Substring(0, i)).Trim() = name))
    /// A generic type by its definition: a type nested in `Outer<T>` has the open `Outer<T>` as
    /// its DeclaringType, which no constructed `Outer<int>` is assignable to.
    let private definition (t: Type) = if t.IsGenericType && not t.IsGenericTypeDefinition then t.GetGenericTypeDefinition() else t
    let rec private within (context: Type) (declaring: Type) =
        not (isNull context) && (definition context = definition declaring || (context.IsNested && within context.DeclaringType declaring))
    let rec private derived (context: Type) (declaring: Type) =
        let d = definition declaring
        let rec bases (t: Type) = not (isNull t) && (definition t = d || bases t.BaseType)
        not (isNull context) && (bases context || (context.IsNested && derived context.DeclaringType declaring))

    /// C#'s qualifier rule for a protected instance member: the receiver's type is the context
    /// (or an enclosing type) or a subclass of it — not the base, nor a sibling derived type.
    /// No receiver (a static member, a nested type) has no such rule.
    let rec private qualifies (context: Type) (receiver: Type) =
        isNull receiver || (not (isNull context) && (context.IsAssignableFrom receiver || (context.IsNested && qualifies context.DeclaringType receiver)))

    /// The C# rule: public; internal from the assembly; protected from a derived type, through
    /// a receiver of that type; protected internal from either; private protected from a
    /// derived type in the assembly; private from inside the declaring type (including nested
    /// types).
    let private accessible (context: Type) (declaring: Type) (receiver: Type) (isPublic, isAssembly, isFamily, isFamilyOrAssembly, isFamilyAndAssembly, isPrivate) =
        let family () = derived context declaring && qualifies context receiver
        isPublic
        || (isAssembly && sameAssembly context declaring)
        || (isFamily && family ())
        || (isFamilyOrAssembly && (sameAssembly context declaring || family ()))
        || (isFamilyAndAssembly && sameAssembly context declaring && family ())
        || (isPrivate && within context declaring)

    /// Whether the type itself can be named from `context`, by the same rule at each nesting
    /// level: C# `dynamic` binds against an inaccessible runtime type (an `internal` class of
    /// another assembly, an anonymous type) as its nearest accessible base, so a public member
    /// of such a type is not found either.
    let rec private typeVisible (context: Type) (t: Type) =
        if isNull t then true
        // A constructed generic is as visible as its arguments (`List<Hidden>` is not).
        elif t.IsConstructedGenericType then
            typeVisible context (t.GetGenericTypeDefinition()) && (t.GetGenericArguments() |> Array.forall (typeVisible context))
        elif t.IsNested then
            accessible context t.DeclaringType null (t.IsNestedPublic, t.IsNestedAssembly, t.IsNestedFamily, t.IsNestedFamORAssem, t.IsNestedFamANDAssem, t.IsNestedPrivate)
            && typeVisible context t.DeclaringType
        else t.IsPublic || sameAssembly context t

    /// `receiver` is the target's runtime type for an instance member (the qualifier rule).
    let method' (context: Type) (receiver: Type) (m: MethodBase) =
        typeVisible context m.DeclaringType
        && accessible context m.DeclaringType (if m.IsStatic then null else receiver) (m.IsPublic, m.IsAssembly, m.IsFamily, m.IsFamilyOrAssembly, m.IsFamilyAndAssembly, m.IsPrivate)

    let field (context: Type) (receiver: Type) (f: FieldInfo) =
        typeVisible context f.DeclaringType
        && accessible context f.DeclaringType (if f.IsStatic then null else receiver) (f.IsPublic, f.IsAssembly, f.IsFamily, f.IsFamilyOrAssembly, f.IsFamilyAndAssembly, f.IsPrivate)

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

/// Two generic helpers over quotations, each the one place its idiom is spelled out.
module internal Quotation =
    /// The method the first call in `e` calls: how a MethodInfo is taken from a quotation of a
    /// call (`<@ f x @>`; a curried static member quotes as an application of a lambda around it).
    let methodOf (e: Expr) : MethodInfo =
        let rec find (e: Expr) =
            match e with
            | Patterns.Call(_, mi, _) -> Some mi
            | ExprShape.ShapeVar _ -> None
            | ExprShape.ShapeLambda(_, body) -> find body
            | ExprShape.ShapeCombination(_, args) -> List.tryPick find args
        match find e with
        | Some mi -> mi
        | None -> invalidArg "e" (sprintf "a quotation of a call, not %A" e)

    /// One level of a rewrite: `f` on each child of `e`, the node rebuilt around the results. The
    /// fallthrough of every walk that rewrites the nodes it knows and recurses into the rest.
    let rebuild (f: Expr -> Expr) (e: Expr) : Expr =
        match e with
        | ExprShape.ShapeVar _ -> e
        | ExprShape.ShapeLambda(v, body) -> Expr.Lambda(v, f body)
        | ExprShape.ShapeCombination(shape, args) -> ExprShape.RebuildShapeCombination(shape, List.map f args)

/// CLR tuples past seven elements, `Tuple` and `ValueTuple` alike: the first seven, then the rest
/// as an eighth, `Rest`, itself a tuple. The one place that nesting is spelled out — for building
/// a tuple's type, reading an element, constructing one in a LINQ tree or a quotation, or at run time.
module internal Tuples =
    let private valueTupleDefinitions =
        [| typedefof<ValueTuple<_>>; typedefof<ValueTuple<_, _>>; typedefof<ValueTuple<_, _, _>>; typedefof<ValueTuple<_, _, _, _>>
           typedefof<ValueTuple<_, _, _, _, _>>; typedefof<ValueTuple<_, _, _, _, _, _>>; typedefof<ValueTuple<_, _, _, _, _, _, _>>
           typedefof<ValueTuple<_, _, _, _, _, _, _, _>> |]

    /// The `ValueTuple` of these element types (one or more).
    let rec valueTupleOf (types: Type list) : Type =
        if types.Length <= 7 then valueTupleDefinitions.[types.Length - 1].MakeGenericType(Array.ofList types)
        else valueTupleDefinitions.[7].MakeGenericType(Array.ofList (List.take 7 types @ [ valueTupleOf (List.skip 7 types) ]))

    /// The member names leading to element `i`: `Item(i+1)`, after a `Rest` per seven before it.
    let rec elementPath (i: int) : string list =
        if i < 7 then [ sprintf "Item%d" (i + 1) ] else "Rest" :: elementPath (i - 7)

    /// Element `i` of a tuple in a LINQ tree (a reference tuple's property, a struct one's field).
    let element (tuple: Expression) (i: int) : Expression =
        elementPath i |> List.fold (fun e name -> Expression.PropertyOrField(e, name) :> Expression) tuple

    /// The fields leading to element `i` of a `ValueTuple` (for a quotation's FieldGet).
    let fieldPath (valueTuple: Type) (i: int) : FieldInfo list =
        elementPath i |> List.mapFold (fun (t: Type) name -> let f = t.GetField name in f, f.FieldType) valueTuple |> fst

    /// The arguments of a tuple constructor: the first seven values and the rest built by `nest`.
    let private split (t: Type) (values: 'E list) (nest: Type -> 'E list -> 'E) =
        let elements = t.GetGenericArguments()
        if elements.Length = 8 then elements, List.take 7 values @ [ nest elements.[7] (List.skip 7 values) ] else elements, values

    /// A tuple of type `t` (reference or struct) from these values, in a LINQ tree.
    let rec newExpression (t: Type) (values: Expression list) : Expression =
        let elements, args = split t values newExpression
        Expression.New(t.GetConstructor elements, args) :> Expression

    /// A tuple of type `t` (reference or struct) from these values, in a quotation.
    let rec newQuotation (t: Type) (values: Expr list) : Expr =
        let elements, args = split t values newQuotation
        Expr.NewObject(t.GetConstructor elements, args)

    /// A tuple of type `t` from these values, at run time: only `Binders.ByRefSite`'s body,
    /// which never runs (see there), so excluded from coverage with it.
    [<ExcludeFromCodeCoverage>]
    let rec make (t: Type) (values: obj list) : obj =
        let _, args = split t values make
        Activator.CreateInstance(t, Array.ofList args)

/// Reference-equality comparer for a pair of types: the default struct-tuple comparer boxes and
/// costs ~100 ns per lookup, which the per-call conversion caches pay each time.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type TypePairComparer() =
    static member val Instance = TypePairComparer()
    interface System.Collections.Generic.IEqualityComparer<struct (Type * Type)> with
        member _.Equals(struct (a1, a2), struct (b1, b2)) = obj.ReferenceEquals(a1, b1) && obj.ReferenceEquals(a2, b2)
        member _.GetHashCode(struct (a, b)) = RuntimeHelpers.GetHashCode a * 31 + RuntimeHelpers.GetHashCode b

/// A delegate type's own members. F# compiles a delegate at the *type's* accessibility, so an
/// `internal` delegate has a non-public `Invoke` and constructor where C#'s stay public; asking for
/// the public one gives null — a null-reference error far from the cause, or a rule that silently
/// does not apply and C#'s own "invalid arguments" in its place (#121, #150).
module internal DelegateMembers =
    let private flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic
    let invokeOf (delegateType: Type) : MethodInfo = delegateType.GetMethod("Invoke", flags)
    /// Whether a delegate of this many parameters has a `Func`/`Action` (17 type parameters, the
    /// result among them); past it the delegate type is one emitted at run time.
    let funcFits (parameterCount: int) = parameterCount + 1 <= 17
    let constructorOf (delegateType: Type) : ConstructorInfo =
        delegateType.GetConstructor(flags, null, [| typeof<obj>; typeof<nativeint> |], null)
    let onNetFramework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith ".NET Framework"
    /// Whether C#'s binder would crash invoking a value of this type: .NET Framework's
    /// `Expression.Invoke` (which C#'s Invoke binder builds) finds `Invoke` by public lookup only,
    /// and throws a NullReferenceException for an F# `internal` delegate's (#151).
    let csharpCannotInvoke (t: Type) =
        onNetFramework && typeof<Delegate>.IsAssignableFrom t && (let i = invokeOf t in not (isNull i) && not i.IsPublic)
    /// .NET Framework's `Expression.Lambda` finds a delegate type's `Invoke` by public lookup only,
    /// so a lambda at an F# `internal` delegate type fails there: the public delegate type of the
    /// same signature to compile it at instead (`Func`/`Action`, or one emitted past sixteen
    /// parameters), which `DelegateLiteral.From` rebinds (#125). None where no stand-in is needed,
    /// or for a byref signature (left as it was).
    let standIn (delegateType: Type) : Type option =
        let invoke = invokeOf delegateType
        let ps = invoke.GetParameters() |> Array.map (fun p -> p.ParameterType)
        if onNetFramework && not invoke.IsPublic && not (ps |> Array.exists (fun p -> p.IsByRef)) then
            Some(Expression.GetDelegateType(Array.append ps [| invoke.ReturnType |]))
        else None

/// When an F# function and a delegate can stand for each other — one rule for both directions.
/// The side that receives a value must accept it: the same type, or for reference types the
/// variance delegates have (a parameter may take a more general type, a result may be a more
/// specific one); no boxing, so value types match exactly. A `void` delegate is a `unit` result,
/// and so is one returning `Unit` itself (`Func<int, unit>`).
module internal Signatures =
    let private accepts (receiver: Type) (given: Type) =
        receiver = given || (not receiver.IsValueType && not given.IsValueType && receiver.IsAssignableFrom given)

    let private results (receiver: Type) (given: Type) (unitResult: bool) (isVoid: bool) =
        if isVoid then unitResult else accepts receiver given

    /// A function of `domains` → `result` serves as a delegate of `parameters` → `returns`: the
    /// delegate's arguments go to the function, its result comes back. A `unit` result serves
    /// only a `void` or `Unit`-returning delegate, as in F# (`fun x -> ()` is no `Func<int, obj>`).
    let functionServesDelegate (domains: Type list) (result: Type) (parameters: Type list) (returns: Type) =
        domains.Length = parameters.Length && List.forall2 accepts domains parameters
        && (if result = typeof<unit> then returns = typeof<Void> || returns = typeof<unit>
            else results returns result false (returns = typeof<Void>))

    /// A delegate of `parameters` → `returns` serves as a function of `domains` → `result`: the
    /// function's arguments go to the delegate, its result comes back.
    let delegateServesFunction (domains: Type list) (result: Type) (parameters: Type list) (returns: Type) =
        domains.Length = parameters.Length && List.forall2 accepts parameters domains
        && results result returns (result = typeof<unit>) (returns = typeof<Void>)

    /// The same signature, no variance: what ranks a candidate among several a delegate or function
    /// argument fits (an exact match beats one through variance, as an exact type does).
    let exactly (domains: Type list) (result: Type) (parameters: Type list) (returns: Type) =
        domains = parameters && (returns = result || (returns = typeof<Void> && result = typeof<unit>))

/// Delegates over IL emitted once — `new Adapter(f)` and a delegate constructor, a rewrap — so a
/// per-call conversion costs an allocation, not `ConstructorInfo.Invoke` or
/// `Delegate.CreateDelegate`'s validation (~150–300 ns). One policy: `skipVisibility`, which covers
/// a non-public member (an F# `internal` delegate's `Invoke` and constructor); and any failure
/// falls back, not only an unsupported platform — the fallback is correct by construction, and a
/// failure inside a static initializer would otherwise poison that type for the process.
module internal Emit =
    let factory<'F when 'F :> Delegate> (name: string) (returnType: Type) (parameters: Type[]) (owner: Module)
                                       (emit: System.Reflection.Emit.ILGenerator -> unit) (fallback: unit -> 'F) : 'F =
        try
            let dm = System.Reflection.Emit.DynamicMethod(name, returnType, parameters, owner, true)
            emit (dm.GetILGenerator())
            dm.CreateDelegate(typeof<'F>) :?> 'F
        with _ -> fallback ()
