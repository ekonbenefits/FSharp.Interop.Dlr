namespace FSharp.Interop.Dlr

open System
open System.Linq.Expressions
open FSharp.Quotations
open FSharp.Quotations.Patterns
open FSharp.Quotations.DerivedPatterns
open FSharp.Quotations.ExprShape
open FSharp.Reflection
open Microsoft.FSharp.Linq.RuntimeHelpers
open System.Runtime.CompilerServices

/// <summary>The state machine's data slot; the builder's <c>Return</c> writes it so the value stays live in the compiled machine. Never read.</summary>
/// <remarks>Not part of the supported API: public only because compiled blocks call it, and it may change in any release.</remarks>
[<Struct; NoComparison; NoEquality; System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type DlrData<'T> =
    [<DefaultValue(false)>]
    val mutable Result: 'T

/// <summary>The compiled block over its state machine, by reference: no copy of the struct at the call,
/// none of the delegate-with-a-struct-argument cost a <c>Func&lt;'SM, 'T&gt;</c> has (measured 4x slower).
/// Public, and not for direct use: an F# <c>internal</c> delegate's <c>Invoke</c> is internal too, and
/// .NET Framework's <c>Expression.Lambda</c> finds <c>Invoke</c> by public lookup only (#125).</summary>
/// <remarks>Not part of the supported API: public only because compiled blocks call it, and it may change in any release.</remarks>
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type DlrReader<'SM, 'T> = delegate of inref<'SM> -> 'T

/// The translator's view of a reflected body: the markers (`opMethod`), the member-operation
/// patterns, evaluation order (`sequenced`) and `normalize`, which applies a piped or curried
/// marker to its arguments before any other rewrite.
module internal TranslatePatterns =
    let unsupported (what: string) (e: Expr) =
        raise (DlrTranslationException(sprintf "dlr { } does not support %s: %A" what e))

    let genericDef (mi: Reflection.MethodInfo) =
        if mi.IsGenericMethod then mi.GetGenericMethodDefinition() else mi

    /// The generic definition of the method a marker quotation calls. Curried static members
    /// quote as applications of an inner lambda, so this looks for the first call anywhere.
    let opMethod (e: Expr<_>) = genericDef (Quotation.methodOf e)

    let opDynamic = opMethod <@ fun (t: obj) (n: string) -> ((?) t n) : obj @>
    let opDynamicAssign = opMethod <@ fun (t: obj) (n: string) (v: obj) -> (?<-) t n v @>
    let opCall = opMethod <@ fun (t: obj) -> (Dlr.call t) : obj @>
    let opApply = opMethod <@ fun (a: obj) (t: obj) -> (Dlr.apply a t) : obj @>
    let opNamed = opMethod <@ fun (r: obj) -> Dlr.named r @>
    let opNamedOf = opMethod <@ fun (l: (string * obj) list) -> Dlr.namedOf l @>
    let opArgsOf = opMethod <@ fun (l: obj list) -> Dlr.argsOf l @>
    /// `Dlr.new'<T>(a, b, …)`: the type and the arguments (each unboxed to its static type).
    let (|New|_|) (e: Expr) =
        match e with
        | Call(None, mi, args) when mi.DeclaringType = typeof<Dlr> && mi.Name = "new'" ->
            Some(mi.GetGenericArguments().[0], [ for a in args -> match a with Coerce(inner, t) when t = typeof<obj> -> inner | a -> a ])
        | _ -> None
    let opCast = opMethod <@ fun (v: obj) -> Dlr.cast<obj> v @>
    let opImplicit = opMethod <@ fun (v: obj) -> (Dlr.implicit v) : obj @>
    let opGet = opMethod <@ fun (n: string) (t: obj) -> (Dlr.get n t) : obj @>
    let opSet = opMethod <@ fun (n: string) (v: obj) (t: obj) -> Dlr.set n v t @>
    let opAddAssign = opMethod <@ fun (n: string) (v: obj) (t: obj) -> Dlr.addAssign n v t @>
    let opSubtractAssign = opMethod <@ fun (n: string) (v: obj) (t: obj) -> Dlr.subtractAssign n v t @>
    let opInvoke = opMethod <@ fun (n: string) (a: obj) (t: obj) -> (Dlr.invoke n a t) : obj @>
    let unaryOps =
        dict [
            opMethod <@ fun (v: obj) -> (Dlr.neg v) : obj @>, ExpressionType.Negate
            opMethod <@ fun (v: obj) -> (Dlr.not v) : obj @>, ExpressionType.Not
            opMethod <@ fun (v: obj) -> (Dlr.complement v) : obj @>, ExpressionType.OnesComplement
        ]
    let forEach = opMethod <@ fun (items: seq<obj>) (body: Func<obj, unit>) -> DlrRuntime.forEach items body @>
    let whileLoop = opMethod <@ fun (guard: Func<bool>) (body: Func<unit>) -> DlrRuntime.whileLoop guard body @>
    let forRange = opMethod <@ fun (low: int) (high: int) (body: Func<int, unit>) -> DlrRuntime.forRange low high body @>
    let tryWith = opMethod <@ fun (body: Func<obj>) (handler: Func<exn, obj>) -> DlrRuntime.tryWith body handler @>
    let tryFinally = opMethod <@ fun (body: Func<obj>) (fin: Func<unit>) -> DlrRuntime.tryFinally body fin @>
    let rethrow = opMethod <@ fun (e: exn) -> (DlrRuntime.rethrow e : obj) @>
    let reraiseMethod = typeof<unit>.Assembly.GetType("Microsoft.FSharp.Core.Operators").GetMethod("Reraise")
    let using = opMethod <@ fun (r: IDisposable) (body: Func<IDisposable, obj>) -> DlrRuntime.using r body @>
    let opItem = opMethod <@ fun (i: obj) (t: obj) -> (Dlr.item i t) : obj @>
    let opSetItem = opMethod <@ fun (i: obj) (v: obj) (t: obj) -> Dlr.setItem i v t @>

    let binaryOps =
        dict [
            opMethod <@ fun (l: obj) (r: obj) -> (l ?%? r) : obj @>, ExpressionType.Modulo
            opMethod <@ fun (l: obj) (r: obj) -> (l ?*? r) : obj @>, ExpressionType.Multiply
            opMethod <@ fun (l: obj) (r: obj) -> (l ?+? r) : obj @>, ExpressionType.Add
            opMethod <@ fun (l: obj) (r: obj) -> (l ?-? r) : obj @>, ExpressionType.Subtract
            opMethod <@ fun (l: obj) (r: obj) -> (l ?/? r) : obj @>, ExpressionType.Divide
            opMethod <@ fun (l: obj) (r: obj) -> (l ?&&&? r) : obj @>, ExpressionType.And
            opMethod <@ fun (l: obj) (r: obj) -> (l ?|||? r) : obj @>, ExpressionType.Or
            opMethod <@ fun (l: obj) (r: obj) -> (l ?^^^? r) : obj @>, ExpressionType.ExclusiveOr
            opMethod <@ fun (l: obj) (r: obj) -> (l ?<<<? r) : obj @>, ExpressionType.LeftShift
            opMethod <@ fun (l: obj) (r: obj) -> (l ?>>>? r) : obj @>, ExpressionType.RightShift
            opMethod <@ fun (l: obj) (r: obj) -> l ?<=? r @>, ExpressionType.LessThanOrEqual
            opMethod <@ fun (l: obj) (r: obj) -> l ?<>? r @>, ExpressionType.NotEqual
            opMethod <@ fun (l: obj) (r: obj) -> l ?<? r @>, ExpressionType.LessThan
            opMethod <@ fun (l: obj) (r: obj) -> l ?=? r @>, ExpressionType.Equal
            opMethod <@ fun (l: obj) (r: obj) -> l ?>? r @>, ExpressionType.GreaterThan
            opMethod <@ fun (l: obj) (r: obj) -> l ?>=? r @>, ExpressionType.GreaterThanOrEqual
        ]

    /// A literal `[ typeof<A>; typeof<B>; … ]`.
    let rec (|TypeOfList|_|) (e: Expr) =
        match e with
        | NewUnionCase(empty, []) when empty.Name = "Empty" -> Some []
        | NewUnionCase(cons, [ Call(None, mi, []); TypeOfList rest ]) when cons.Name = "Cons" && mi.Name = "TypeOf" && mi.IsGenericMethod ->
            Some(mi.GetGenericArguments().[0] :: rest)
        | _ -> None

    /// The explicit type arguments of a call: known at translation time (`Dlr.typeArgs<A, B>()`,
    /// or `Dlr.typeArgsOf` with a literal list of `typeof`s, which folds to the same), or an
    /// expression evaluated per call (`Dlr.typeArgsOf ts`), which keys a SiteCache like a
    /// computed name does.
    type TypeArgsSpec =
        | StaticTypes of Type list
        | RuntimeTypes of Expr

    let (|TypeArgs|_|) (e: Expr) =
        match e with
        | Call(None, mi, []) when mi.DeclaringType = typeof<Dlr> && mi.Name = "typeArgs" -> Some(StaticTypes(List.ofArray (mi.GetGenericArguments())))
        | Call(None, mi, [ TypeOfList ts ]) when mi.DeclaringType = typeof<Dlr> && mi.Name = "typeArgsOf" -> Some(StaticTypes ts)
        | Call(None, mi, [ types ]) when mi.DeclaringType = typeof<Dlr> && mi.Name = "typeArgsOf" -> Some(RuntimeTypes types)
        | _ -> None

    /// `Static<T>.Overloads` as a target: the type.
    let (|StaticTarget|_|) (e: Expr) =
        match e with
        | PropertyGet(None, pi, []) when pi.Name = "Overloads" && pi.DeclaringType.IsGenericType && pi.DeclaringType.GetGenericTypeDefinition() = typedefof<Dlr.Static<_>> ->
            Some(pi.DeclaringType.GetGenericArguments().[0])
        | _ -> None

    let (|Op|_|) (def: Reflection.MethodInfo) (e: Expr) =
        match e with
        | Call(None, mi, args) when genericDef mi = def -> Some args
        | _ -> None

    /// `Dlr.namedOf pairs`: the list expression.
    let (|NamedOf|_|) (e: Expr) =
        match e with
        | Op opNamedOf [ pairs ] -> Some pairs
        | _ -> None

    /// `Dlr.argsOf values`: the list expression.
    let (|ArgsOf|_|) (e: Expr) =
        match e with
        | Op opArgsOf [ values ] -> Some values
        | _ -> None

    /// Either splat marker.
    let isSplat (e: Expr) = match e with NamedOf _ | ArgsOf _ -> true | _ -> false

    /// `Dlr.out` / `Dlr.outAs<'T> ()`: an out argument, its value returned in the result tuple; with
    /// the type the out states (`outAs`), or None for the result's shape to infer.
    let (|OutMarker|_|) (e: Expr) =
        match e with
        | PropertyGet(None, p, []) when p.DeclaringType = typeof<Dlr> && p.Name = "out" -> Some None
        | Call(None, mi, []) when mi.DeclaringType = typeof<Dlr> && mi.Name = "outAs" -> Some(Some(mi.GetGenericArguments().[0]))
        | _ -> None

    /// `Dlr.ref v`: a ref argument over the variable `v`.
    let (|RefMarker|_|) (e: Expr) =
        match e with
        | Call(None, mi, [ v ]) when mi.DeclaringType = typeof<Dlr> && mi.Name = "ref" -> Some v
        | _ -> None

    let isByRefMarker (e: Expr) = match e with OutMarker _ | RefMarker _ -> true | _ -> false
    let unboxTo = opMethod <@ fun (o: obj) -> unbox<int> o @>

    let (|UnaryOp|_|) (e: Expr) =
        match e with
        | Call(None, mi, [ v ]) ->
            match unaryOps.TryGetValue(genericDef mi) with
            | true, op -> Some(op, v)
            | _ -> None
        | _ -> None

    let (|BinaryOp|_|) (e: Expr) =
        match e with
        | Call(None, mi, [ l; r ]) ->
            match binaryOps.TryGetValue(genericDef mi) with
            | true, op -> Some(op, l, r)
            | _ -> None
        | _ -> None

    let (|Literal|_|) (e: Expr) =
        match e with
        | Value(o, _) -> Some o
        | _ -> None


    /// Strips the boxing F# inserts on the way to an `obj` parameter, so the binder can see the
    /// real static type.
    let (|Unboxed|) (e: Expr) =
        match e with
        | Coerce(inner, t) when t = typeof<obj> -> inner
        | _ -> e

    /// `Dlr.named {| a = x; b = y |}`: the field names and values. F# evaluates the fields in
    /// source order through `let` temporaries and then builds the record in its own (sorted)
    /// field order, so the temporaries come back as bindings to wrap around the call.
    let (|NamedRecord|_|) (e: Expr) =
        let rec peel (bindings: (Var * Expr) list) (e: Expr) =
            match e with
            | Let(v, value, body) -> peel ((v, value) :: bindings) body
            | Op opNamed [ inner ] -> peel bindings inner
            | NewRecord(recordType, values) ->
                let fields = FSharpType.GetRecordFields recordType
                Some(List.rev bindings, List.zip [ for f in fields -> f.Name ] values)
            | other -> unsupported "Dlr.named applied to anything but an anonymous record literal" other
        match e with
        | Op opNamed _ -> peel [] e
        | _ -> None

    /// The compiler eta-expands a dynamic member used as a statement:
    /// `let clo = x?Foo in fun a -> clo a` applied to `()`. Fold it back to `x?Foo`.
    let rec (|EtaReduced|) (e: Expr) =
        match e with
        | Let(v, value, Lambda(x, Application(Var v', Var x'))) when v = v' && x = x' -> value
        | _ -> e

    /// `let a0 = x.0 in … let an = x.n in v (a0, …, an)`: the lambda body of the tupled
    /// eta-expansion the compiler makes of a call with a tuple of arguments on a target whose
    /// static type is not `obj` — a struct or class expression, a typed variable — (`let clo =
    /// (f ())?M in fun tupledArg -> …` applied to the tuple).
    let retuples (v: Var) (x: Var) (body: Expr) =
        let rec peel (elements: Var list) (e: Expr) =
            match e with
            | Let(a, TupleGet(Var x', i), rest) when x' = x && i = elements.Length -> peel (elements @ [ a ]) rest
            | Application(Var v', NewTuple items) when v' = v ->
                items.Length = elements.Length && List.forall2 (fun (item: Expr) (a: Var) -> item = Expr.Var a) items elements
            | _ -> false
        peel [] body

    /// One of the member operations, whichever way it was spelled: `x?Name` / `(?) x name` /
    /// `Dlr.get name x`, those applied to arguments (`x?Name(a)`, `(Dlr.get name x)(a)`) or
    /// `Dlr.invoke name a x`, and `x?Name <- v` / `Dlr.set name v x`. Target, name
    /// expression, and the argument expression (for an invocation) or value (for a set).
    type MemberOp =
        | GetMember of target: Expr * name: Expr
        | InvokeMember of target: Expr * name: Expr * args: Expr
        | SetMember of target: Expr * name: Expr * value: Expr

    /// An expression typed as a delegate type a member can be read as (#201), with the function
    /// type its invoker takes past fourteen parameters (`Binders.delegateRead`).
    let (|DelegateRead|_|) (e: Expr) = Binders.delegateRead e.Type

    let (|MemberOp|_|) (e: Expr) =
        match e with
        | Application(EtaReduced(Op opDynamic [ Unboxed target; name ]), args) -> Some(InvokeMember(target, name, args))
        | Op opDynamic [ Unboxed target; name ] -> Some(GetMember(target, name))
        | Op opDynamicAssign [ Unboxed target; name; value ] -> Some(SetMember(target, name, value))
        | Application(EtaReduced(Op opGet [ name; Unboxed target ]), args) -> Some(InvokeMember(target, name, args))
        | Op opGet [ name; Unboxed target ] -> Some(GetMember(target, name))
        | Op opInvoke [ name; args; Unboxed target ] -> Some(InvokeMember(target, name, args))
        | Op opSet [ name; value; Unboxed target ] -> Some(SetMember(target, name, value))
        | _ -> None

    /// `x?Foo()` applies unit; `x?Foo(a, b)` applies a tuple; `x?Foo(a)` applies one value. As in
    /// F#'s own method calls (`w.Add args` with `args: int * int`), an expression whose static
    /// type is a reference tuple is several arguments too: it is bound once and split with
    /// `TupleGet`, each element keeping its static type. A struct tuple is one value, as in F#;
    /// `box t` passes a tuple as one dynamic argument. Returns the binding, if any, and the items.
    let splitArgs (e: Expr) : (Var * Expr) list * Expr list =
        match e with
        // `()`, or a `unit`-typed variable (a generic parameter instantiated to unit, `let args = ()`):
        // no arguments, and nothing to evaluate.
        | Value(_, t) when t = typeof<unit> -> [], []
        | Var v when v.Type = typeof<unit> -> [], []
        // A unit-valued expression: evaluated for its effect, and no arguments. A void call
        // cannot be let-bound (the converter has no value for it), so `()` follows it.
        | e when e.Type = typeof<unit> -> [ Var("effect", typeof<unit>), Expr.Sequential(e, Expr.Value(())) ], []
        | NewTuple items -> [], items
        | _ when FSharpType.IsTuple e.Type && not e.Type.IsValueType ->
            let v = Var("args", e.Type)
            [ v, e ], [ for i in 0 .. FSharpType.GetTupleElements(e.Type).Length - 1 -> Expr.TupleGet(Expr.Var v, i) ]
        | single -> [], [ single ]

    /// Whether evaluating `e` has no effect and no order to keep: an immutable variable, a
    /// literal, a lambda, a read-only field, a tuple or coercion of such. A mutable (a captured
    /// `let mutable`, a mutable field) is not: an argument may assign it. Anything else is bound
    /// in source order by `sequenced`.
    let rec isPure (e: Expr) =
        match e with
        | Var v -> not v.IsMutable
        | Value _ | Lambda _ | StaticTarget _ -> true
        | FieldGet(None, f) -> f.IsInitOnly || f.IsLiteral
        | FieldGet(Some inner, f) -> f.IsInitOnly && isPure inner
        | Coerce(inner, _) | TupleGet(inner, _) -> isPure inner
        | NewTuple items -> List.forall isPure items
        | _ -> false

    /// Whether a form's arguments make it hoist something ahead of its site call: a `Dlr.named`
    /// record's field temporaries, a splat list, a tuple bound by `splitArgs`.
    let hoists (tupleBindings: (Var * Expr) list) (argExprs: Expr list) =
        not tupleBindings.IsEmpty
        || argExprs |> List.exists (fun a -> match a with NamedOf _ | ArgsOf _ -> true | NamedRecord(lets, _) -> not lets.IsEmpty | _ -> false)

    /// C#'s evaluation order — the target, then the arguments left to right — kept where a form
    /// hoists something ahead of its site call (`hoists`), which would otherwise run first:
    /// every impure expression is bound to a variable in source order, so the hoisted ones take
    /// their own place. The tuple of `splitArgs` goes after the target, where the arguments are;
    /// a computed name or type list (`keys`) between the target and the arguments, where
    /// `(?) x name args` writes them. Returns the bindings, the variables to add to the bound
    /// set, and the target, keys and arguments rewritten over the variables.
    let sequenced (target: Expr option) (keys: Expr list) (tupleBindings: (Var * Expr) list) (argExprs: Expr list) =
        let bindings = ResizeArray<Var * Expr>()
        let place (name: string) (e: Expr) =
            if isPure e then e
            else
                let v = Var(name, e.Type)
                bindings.Add((v, e))
                Expr.Var v
        let target' = target |> Option.map (place "target")
        let keys' = keys |> List.mapi (fun i k -> place (sprintf "key%d" i) k)
        bindings.AddRange tupleBindings
        let args' =
            argExprs |> List.mapi (fun i a ->
                match a with
                // The record's temporaries at the record's place; the record itself then holds
                // only variables and pure values.
                | NamedRecord(lets, _) when not lets.IsEmpty ->
                    let rec strip (e: Expr) =
                        match e with
                        | Let(_, _, body) -> strip body
                        | Call(None, mi, [ inner ]) -> Expr.Call(mi, [ strip inner ])
                        | e -> e
                    bindings.AddRange lets
                    strip a
                | NamedRecord _ -> a
                | Call(None, mi, [ list ]) when isSplat a -> Expr.Call(mi, [ place (sprintf "list%d" i) list ])
                // A byref marker stays where it is: the translator reads a ref's variable at the
                // call, after every other argument (a C# ref is a reference: the callee sees each
                // argument's write to it).
                | OutMarker _ | RefMarker _ -> a
                | a -> place (sprintf "arg%d" i) a)
        let bound = bindings |> Seq.map fst |> Set.ofSeq
        List.ofSeq bindings, bound, target', keys', args'

    /// The definition of a let-bound variable somewhere in `e` (quotation Vars are identity-based,
    /// so shadowing is not a concern). A `let rec` one is its whole group, `let rec … in v`: its
    /// own references stay bound to it rather than recovered again (#198).
    let rec letDefinition (v: Var) (e: Expr) : Expr option =
        match e with
        | Let(v', def, _) when v' = v -> Some def
        | LetRecursive(bindings, _) when bindings |> List.exists (fun (v', _) -> v' = v) -> Some(Expr.LetRecursive(bindings, Expr.Var v))
        | ShapeVar _ -> None
        | ShapeLambda(_, body) -> letDefinition v body
        | ShapeCombination(_, args) -> args |> List.tryPick (letDefinition v)

    /// The argument a parameter of a let-bound local function takes, when the member body
    /// applies that function exactly once — or of a lambda applied on the spot,
    /// `(fun k -> …) 1`: the optimizer inlines such a function at its call site, so its
    /// parameters become that call's arguments and are never captured.
    let parameterArgument (v: Var) (memberBody: Expr) : Expr option =
        let rec lambdaParams (e: Expr) =
            match e with
            | Lambda(p, body) -> let ps, inner = lambdaParams body in p :: ps, inner
            | _ -> [], e
        let rec applications (f: Var) (e: Expr) : Expr list list =
            // Full application chains f a1 a2 … of `f`, innermost first.
            let rec chain (e: Expr) (acc: Expr list) =
                match e with
                | Application(inner, arg) -> chain inner (arg :: acc)
                | Var f' when f' = f -> Some acc
                | _ -> None
            match chain e [] with
            | Some args when not args.IsEmpty -> [ args ]
            | _ ->
                match e with
                | ShapeVar _ -> []
                | ShapeLambda(_, body) -> applications f body
                | ShapeCombination(_, args) -> args |> List.collect (applications f)
        /// `(fun p1 p2 -> body) a1 a2`: the lambda chain and its arguments, in order.
        let rec applied (e: Expr) (acc: Expr list) =
            match e with
            | Application(inner, arg) -> applied inner (arg :: acc)
            | Lambda _ when not acc.IsEmpty -> Some(e, acc)
            | _ -> None
        /// The argument `v` takes in a lambda applied on the spot, if `e` is one binding `v`.
        let appliedArgument (e: Expr) : Expr option =
            match applied e [] with
            | Some(f, args) ->
                let ps, _ = lambdaParams f
                match List.tryFindIndex ((=) v) ps with
                | Some i when i < args.Length -> Some args.[i]
                | _ -> None
            | None -> None
        let rec search (e: Expr) : Expr option =
            match e with
            | Let(f, def, rest) when (let ps, _ = lambdaParams def in List.contains v ps) ->
                let ps, _ = lambdaParams def
                let index = List.findIndex ((=) v) ps
                match applications f rest with
                | [ args ] when args.Length > index -> Some args.[index]
                | [] -> None
                | _ -> None
            | Application _ when (appliedArgument e).IsSome -> appliedArgument e
            | ShapeVar _ -> None
            | ShapeLambda(_, body) -> search body
            | ShapeCombination(_, args) -> args |> List.tryPick search
        search memberBody

    // `|>` is inlined inside a quotation literal, so these come from reflection instead.
    let fsharpOperators = typeof<obj list>.Assembly.GetType("Microsoft.FSharp.Core.Operators")
    let pipeRight = fsharpOperators.GetMethod("op_PipeRight")
    let pipeLeft = fsharpOperators.GetMethod("op_PipeLeft")

    /// `x |> f` in a reflected body is `op_PipeRight(x, let name = "A" in fun target -> …)`, and a
    /// curried marker applied to its arguments is `Application(Lambda(name, Lambda(target, …)), …)`.
    /// Apply such functions to their arguments and inline `let`s of literals and variables, so
    /// `w |> Dlr.get "A"` becomes the plain `Dlr.get "A" w` call the member-op patterns recognise,
    /// with the name a literal and a tuple argument still a tuple. An argument is substituted for
    /// its parameter only when that cannot change what runs: it is a variable or a literal, or it
    /// is used exactly once and not under a lambda (where it would run per invocation). Otherwise
    /// it is let-bound, so it is evaluated once — including when the parameter is never used.
    let rec normalize (e: Expr) : Expr =
        /// Occurrences of `v`, with any occurrence under a lambda counted as many.
        let rec occurrences (v: Var) (e: Expr) =
            match e with
            | Var v' when v' = v -> 1
            | ShapeVar _ -> 0
            | ShapeLambda(_, body) -> occurrences v body * 2
            | ShapeCombination(_, args) -> args |> List.sumBy (occurrences v)
        /// The same, but lambdas on the spine of a curried function (`fun a -> fun b -> …`, each
        /// applied exactly once by the next argument) do not count as "under a lambda".
        let rec spineOccurrences (v: Var) (e: Expr) =
            match e with
            | Lambda(_, body) -> spineOccurrences v body
            | _ -> occurrences v e
        let pure' (x: Expr) = match x with Var v -> not v.IsMutable | Value _ -> true | _ -> false
        let rec apply (f: Expr) (x: Expr) : Expr =
            match f with
            | Let(v, value, body) -> Expr.Let(v, value, apply body x)
            | Lambda(p, body) ->
                if pure' x || spineOccurrences p body = 1 then body.Substitute(fun v -> if v = p then Some x else None)
                // A `unit` argument may compile to a void site call, which cannot be let-bound:
                // run it, then the body with `()` for the parameter.
                elif x.Type = typeof<unit> then Expr.Sequential(x, body.Substitute(fun v -> if v = p then Some(Expr.Value(())) else None))
                else Expr.Let(p, x, body)
            | _ -> Expr.Application(f, x)
        let applied (f: Expr) (x: Expr) =
            match normalize f, normalize x with
            | (Lambda _ | Let _ as f), x -> normalize (apply f x)
            | f, x -> Expr.Application(f, x)
        match e with
        | Op pipeRight [ x; f ] -> applied f x
        | Op pipeLeft [ f; x ] -> applied f x
        // The tupled eta-expansion of a member call: back to the member applied to the tuple,
        // before the tuple would be let-bound under the lambda and its markers lost.
        | Application(Let(v, value, Lambda(x, body)), arg) when retuples v x body -> applied value arg
        | Application(f, x) -> applied f x
        // A `let` of a literal or an immutable variable is inlined; a snapshot of a mutable
        // (`let y = n` with `n` mutable) is not, since `n` may change before `y` is used.
        | Let(v, (Value _ | Var _ as value), body) when not v.IsMutable && pure' value -> normalize (body.Substitute(fun v' -> if v' = v then Some value else None))
        // The eta-expanded statement form `let clo = x?Foo in clo ()` once its lambda is applied.
        | Let(v, value, Application(Var v', arg)) when v = v' -> applied value arg
        // A parameterless delegate literal (`Func<int>(fun () -> 7)`, `Action(fun () -> …)`): F#
        // quotes it with no parameter and a bare body, a node FSharp.Core takes apart as the lambda
        // `fun () -> body` but rebuilds only from the bare body, so no rewrite can pass it through
        // (#156). It becomes `ParameterlessLiteral<D, R>.Of (fun () -> body)`, an ordinary call
        // that makes the delegate by its own type.
        | NewDelegate(t, [], body) ->
            let body = normalize body
            let thunk = Expr.Lambda(Var("unitVar", typeof<unit>), body)
            let literal = typedefof<ParameterlessLiteral<_, _>>.MakeGenericType(t, body.Type)
            Expr.Call(literal.GetMethod("Of"), [ thunk ])
        | _ -> Quotation.rebuild normalize e
