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
[<Struct; NoComparison; NoEquality; System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type DlrData<'T> =
    [<DefaultValue(false)>]
    val mutable Result: 'T

/// <summary>The compiled block over its state machine, by reference: no copy of the struct at the call,
/// none of the delegate-with-a-struct-argument cost a <c>Func&lt;'SM, 'T&gt;</c> has (measured 4x slower).
/// Public, and not for direct use: an F# <c>internal</c> delegate's <c>Invoke</c> is internal too, and
/// .NET Framework's <c>Expression.Lambda</c> finds <c>Invoke</c> by public lookup only (#125).</summary>
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type DlrReader<'SM, 'T> = delegate of inref<'SM> -> 'T

/// Turns the reflected body of a `dlr { }` block into a delegate over the block's compiler-generated
/// container — a `DlrReader<'SM, 'T>` over its state machine struct, or a `Func<obj, 'T>` over its
/// Delay closure — with the DLR call sites baked in as constants.
module internal Translate =

    /// A compiled `dlr { }` site: a `DlrReader<'SM, 'T>` over the state machine struct, or in the
    /// fallback path a `Func<obj, 'T>` over the Delay closure object.
    type Compiled =
        { Delegate: Delegate
          ResultType: Type }

    let private unsupported (what: string) (e: Expr) =
        raise (DlrTranslationException(sprintf "dlr { } does not support %s: %A" what e))

    let private genericDef (mi: Reflection.MethodInfo) =
        if mi.IsGenericMethod then mi.GetGenericMethodDefinition() else mi

    /// The generic definition of the method a marker quotation calls. Curried static members
    /// quote as applications of an inner lambda, so this looks for the first call anywhere.
    let private opMethod (e: Expr<_>) =
        let rec find (e: Expr) =
            match e with
            | Call(None, mi, _) -> Some(genericDef mi)
            | ShapeVar _ -> None
            | ShapeLambda(_, body) -> find body
            | ShapeCombination(_, args) -> args |> List.tryPick find
        match find e with
        | Some mi -> mi
        | None -> failwith "operator definition expected"

    let private opDynamic = opMethod <@ fun (t: obj) (n: string) -> ((?) t n) : obj @>
    let private opDynamicAssign = opMethod <@ fun (t: obj) (n: string) (v: obj) -> (?<-) t n v @>
    let private opCall = opMethod <@ fun (t: obj) -> (Dlr.call t) : obj @>
    let private opApply = opMethod <@ fun (a: obj) (t: obj) -> (Dlr.apply a t) : obj @>
    let private opNamed = opMethod <@ fun (r: obj) -> Dlr.named r @>
    let private opNamedOf = opMethod <@ fun (l: (string * obj) list) -> Dlr.namedOf l @>
    let private opArgsOf = opMethod <@ fun (l: obj list) -> Dlr.argsOf l @>
    /// `Dlr.new'<T>(a, b, …)`: the type and the arguments (each unboxed to its static type).
    let private (|New|_|) (e: Expr) =
        match e with
        | Call(None, mi, args) when mi.DeclaringType = typeof<Dlr> && mi.Name = "new'" ->
            Some(mi.GetGenericArguments().[0], [ for a in args -> match a with Coerce(inner, t) when t = typeof<obj> -> inner | a -> a ])
        | _ -> None
    let private opCast = opMethod <@ fun (v: obj) -> Dlr.cast<obj> v @>
    let private opImplicit = opMethod <@ fun (v: obj) -> (Dlr.implicit v) : obj @>
    let private opGet = opMethod <@ fun (n: string) (t: obj) -> (Dlr.get n t) : obj @>
    let private opSet = opMethod <@ fun (n: string) (v: obj) (t: obj) -> Dlr.set n v t @>
    let private opAddAssign = opMethod <@ fun (n: string) (v: obj) (t: obj) -> Dlr.addAssign n v t @>
    let private opSubtractAssign = opMethod <@ fun (n: string) (v: obj) (t: obj) -> Dlr.subtractAssign n v t @>
    let private opInvoke = opMethod <@ fun (n: string) (a: obj) (t: obj) -> (Dlr.invoke n a t) : obj @>
    let private unaryOps =
        dict [
            opMethod <@ fun (v: obj) -> (Dlr.neg v) : obj @>, ExpressionType.Negate
            opMethod <@ fun (v: obj) -> (Dlr.not v) : obj @>, ExpressionType.Not
            opMethod <@ fun (v: obj) -> (Dlr.complement v) : obj @>, ExpressionType.OnesComplement
        ]
    let private forEach = opMethod <@ fun (items: seq<obj>) (body: Func<obj, unit>) -> DlrRuntime.forEach items body @>
    let private whileLoop = opMethod <@ fun (guard: Func<bool>) (body: Func<unit>) -> DlrRuntime.whileLoop guard body @>
    let private tryWith = opMethod <@ fun (body: Func<obj>) (handler: Func<exn, obj>) -> DlrRuntime.tryWith body handler @>
    let private tryFinally = opMethod <@ fun (body: Func<obj>) (fin: Func<unit>) -> DlrRuntime.tryFinally body fin @>
    let private using = opMethod <@ fun (r: IDisposable) (body: Func<IDisposable, obj>) -> DlrRuntime.using r body @>
    let private opItem = opMethod <@ fun (i: obj) (t: obj) -> (Dlr.item i t) : obj @>
    let private opSetItem = opMethod <@ fun (i: obj) (v: obj) (t: obj) -> Dlr.setItem i v t @>

    let private binaryOps =
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
    let rec private (|TypeOfList|_|) (e: Expr) =
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

    let private (|TypeArgs|_|) (e: Expr) =
        match e with
        | Call(None, mi, []) when mi.DeclaringType = typeof<Dlr> && mi.Name = "typeArgs" -> Some(StaticTypes(List.ofArray (mi.GetGenericArguments())))
        | Call(None, mi, [ TypeOfList ts ]) when mi.DeclaringType = typeof<Dlr> && mi.Name = "typeArgsOf" -> Some(StaticTypes ts)
        | Call(None, mi, [ types ]) when mi.DeclaringType = typeof<Dlr> && mi.Name = "typeArgsOf" -> Some(RuntimeTypes types)
        | _ -> None

    /// `Static<T>.Overloads` as a target: the type.
    let private (|StaticTarget|_|) (e: Expr) =
        match e with
        | PropertyGet(None, pi, []) when pi.Name = "Overloads" && pi.DeclaringType.IsGenericType && pi.DeclaringType.GetGenericTypeDefinition() = typedefof<Dlr.Static<_>> ->
            Some(pi.DeclaringType.GetGenericArguments().[0])
        | _ -> None

    let private (|Op|_|) (def: Reflection.MethodInfo) (e: Expr) =
        match e with
        | Call(None, mi, args) when genericDef mi = def -> Some args
        | _ -> None

    /// `Dlr.namedOf pairs`: the list expression.
    let private (|NamedOf|_|) (e: Expr) =
        match e with
        | Op opNamedOf [ pairs ] -> Some pairs
        | _ -> None

    /// `Dlr.argsOf values`: the list expression.
    let private (|ArgsOf|_|) (e: Expr) =
        match e with
        | Op opArgsOf [ values ] -> Some values
        | _ -> None

    /// Either splat marker.
    let private isSplat (e: Expr) = match e with NamedOf _ | ArgsOf _ -> true | _ -> false

    /// `Dlr.out`: an out argument, its value returned in the result tuple.
    let private (|OutMarker|_|) (e: Expr) =
        match e with
        | PropertyGet(None, p, []) when p.DeclaringType = typeof<Dlr> && p.Name = "out" -> Some()
        | _ -> None

    /// `Dlr.ref v`: a ref argument over the variable `v`.
    let private (|RefMarker|_|) (e: Expr) =
        match e with
        | Call(None, mi, [ v ]) when mi.DeclaringType = typeof<Dlr> && mi.Name = "ref" -> Some v
        | _ -> None

    let private isByRefMarker (e: Expr) = match e with OutMarker | RefMarker _ -> true | _ -> false
    let private arrayGet = opMethod <@ fun (a: obj[]) -> a.[0] @>
    let private unboxTo = opMethod <@ fun (o: obj) -> unbox<int> o @>

    let private (|UnaryOp|_|) (e: Expr) =
        match e with
        | Call(None, mi, [ v ]) ->
            match unaryOps.TryGetValue(genericDef mi) with
            | true, op -> Some(op, v)
            | _ -> None
        | _ -> None

    let private (|BinaryOp|_|) (e: Expr) =
        match e with
        | Call(None, mi, [ l; r ]) ->
            match binaryOps.TryGetValue(genericDef mi) with
            | true, op -> Some(op, l, r)
            | _ -> None
        | _ -> None

    let private (|Literal|_|) (e: Expr) =
        match e with
        | Value(o, _) -> Some o
        | _ -> None

    let private isLiteral (e: Expr) = match e with Value _ -> true | _ -> false

    /// Strips the boxing F# inserts on the way to an `obj` parameter, so the binder can see the
    /// real static type.
    let private (|Unboxed|) (e: Expr) =
        match e with
        | Coerce(inner, t) when t = typeof<obj> -> inner
        | _ -> e

    /// `Dlr.named {| a = x; b = y |}`: the field names and values. F# evaluates the fields in
    /// source order through `let` temporaries and then builds the record in its own (sorted)
    /// field order, so the temporaries come back as bindings to wrap around the call.
    let private (|NamedRecord|_|) (e: Expr) =
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
    let rec private (|EtaReduced|) (e: Expr) =
        match e with
        | Let(v, value, Lambda(x, Application(Var v', Var x'))) when v = v' && x = x' -> value
        | _ -> e

    /// `let a0 = x.0 in … let an = x.n in v (a0, …, an)`: the lambda body of the tupled
    /// eta-expansion the compiler makes of a call with a tuple of arguments on a target whose
    /// static type is not `obj` — a struct or class expression, a typed variable — (`let clo =
    /// (f ())?M in fun tupledArg -> …` applied to the tuple).
    let private retuples (v: Var) (x: Var) (body: Expr) =
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
    type private MemberOp =
        | GetMember of target: Expr * name: Expr
        | InvokeMember of target: Expr * name: Expr * args: Expr
        | SetMember of target: Expr * name: Expr * value: Expr

    let private (|MemberOp|_|) (e: Expr) =
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
    let private splitArgs (e: Expr) : (Var * Expr) list * Expr list =
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
    let rec private isPure (e: Expr) =
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
    let private hoists (tupleBindings: (Var * Expr) list) (argExprs: Expr list) =
        not tupleBindings.IsEmpty
        || argExprs |> List.exists (fun a -> match a with NamedOf _ | ArgsOf _ -> true | NamedRecord(lets, _) -> not lets.IsEmpty | _ -> false)

    /// C#'s evaluation order — the target, then the arguments left to right — kept where a form
    /// hoists something ahead of its site call (`hoists`), which would otherwise run first:
    /// every impure expression is bound to a variable in source order, so the hoisted ones take
    /// their own place. The tuple of `splitArgs` goes after the target, where the arguments are;
    /// a computed name or type list (`keys`) between the target and the arguments, where
    /// `(?) x name args` writes them. Returns the bindings, the variables to add to the bound
    /// set, and the target, keys and arguments rewritten over the variables.
    let private sequenced (target: Expr option) (keys: Expr list) (tupleBindings: (Var * Expr) list) (argExprs: Expr list) =
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
                | a -> place (sprintf "arg%d" i) a)
        let bound = bindings |> Seq.map fst |> Set.ofSeq
        List.ofSeq bindings, bound, target', keys', args'

    /// The definition of a let-bound variable somewhere in `e` (quotation Vars are identity-based,
    /// so shadowing is not a concern).
    let rec private letDefinition (v: Var) (e: Expr) : Expr option =
        match e with
        | Let(v', def, _) when v' = v -> Some def
        | ShapeVar _ -> None
        | ShapeLambda(_, body) -> letDefinition v body
        | ShapeCombination(_, args) -> args |> List.tryPick (letDefinition v)

    /// The argument a parameter of a let-bound local function takes, when the member body
    /// applies that function exactly once — or of a lambda applied on the spot,
    /// `(fun k -> …) 1`: the optimizer inlines such a function at its call site, so its
    /// parameters become that call's arguments and are never captured.
    let private parameterArgument (v: Var) (memberBody: Expr) : Expr option =
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
    let private fsharpOperators = typeof<obj list>.Assembly.GetType("Microsoft.FSharp.Core.Operators")
    let private pipeRight = fsharpOperators.GetMethod("op_PipeRight")
    let private pipeLeft = fsharpOperators.GetMethod("op_PipeLeft")

    /// `x |> f` in a reflected body is `op_PipeRight(x, let name = "A" in fun target -> …)`, and a
    /// curried marker applied to its arguments is `Application(Lambda(name, Lambda(target, …)), …)`.
    /// Apply such functions to their arguments and inline `let`s of literals and variables, so
    /// `w |> Dlr.get "A"` becomes the plain `Dlr.get "A" w` call the member-op patterns recognise,
    /// with the name a literal and a tuple argument still a tuple. An argument is substituted for
    /// its parameter only when that cannot change what runs: it is a variable or a literal, or it
    /// is used exactly once and not under a lambda (where it would run per invocation). Otherwise
    /// it is let-bound, so it is evaluated once — including when the parameter is never used.
    let rec private normalize (e: Expr) : Expr =
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
        | ShapeVar _ -> e
        | ShapeLambda(v, body) -> Expr.Lambda(v, normalize body)
        | ShapeCombination(shape, args) -> RebuildShapeCombination(shape, List.map normalize args)

    /// The call sites of a compiled block hoisted into locals of the lambda that uses them.
    /// `LambdaExpression.Compile` keeps a reference-type constant in its closure's `Constants`
    /// array and re-reads and casts it at every use — two per site call (`site.Target` and the
    /// `site` argument). This visitor gives each lambda — the block's own and every nested one
    /// (loop and try bodies, user lambdas) — a `Block` binding the sites its body uses directly to
    /// variables assigned once at entry, so a use is a local read. Per lambda, not at the block's
    /// entry: a variable captured by a nested lambda would be a `StrongBox` read, no better than
    /// the constant. Done on the LINQ tree rather than as quotation `Let`s, which FSharp.Core
    /// before 10.1 converts to nested lambda invocations (50× slower, measured).
    type private SiteHoister() =
        inherit ExpressionVisitor()
        let mutable current : System.Collections.Generic.Dictionary<CallSite, ParameterExpression> = null

        override this.VisitLambda<'T>(node: Expression<'T>) : Expression =
            let saved = current
            current <- System.Collections.Generic.Dictionary(HashIdentity.Reference)
            let body = this.Visit node.Body
            let mine = current
            current <- saved
            if mine.Count = 0 then node.Update(body, node.Parameters) :> Expression
            else
                let assigns = [ for KeyValue(site, var) in mine -> Expression.Assign(var, Expression.Constant(site, var.Type)) :> Expression ]
                let block = Expression.Block(body.Type, mine.Values, assigns @ [ body ])
                node.Update(block, node.Parameters) :> Expression

        override _.VisitConstant(node: ConstantExpression) : Expression =
            match node.Value with
            | :? CallSite as site when not (isNull current) ->
                match current.TryGetValue site with
                | true, var -> var :> Expression
                | _ ->
                    let var = Expression.Variable(node.Type, "site")
                    current.[site] <- var
                    var :> Expression
            | _ -> node :> Expression

        /// A wide site's placeholder (`Binders.WideSite`) becomes the typed `Invoke` on the site's
        /// `Target`: the delegate type from the placeholder's constant, each argument unboxed
        /// back to the parameter type it was boxed from.
        override this.VisitMethodCall(node: MethodCallExpression) : Expression =
            if node.Method.DeclaringType = typeof<Binders.WideSite> then
                let site = this.Visit node.Arguments.[0]
                let delegateType = (node.Arguments.[1] :?> ConstantExpression).Value :?> Type
                let parameters = delegateType.GetMethod("Invoke").GetParameters()
                let elements = (node.Arguments.[2] :?> NewArrayExpression).Expressions
                let args =
                    [ for i in 0 .. elements.Count - 1 ->
                        let wanted = parameters.[i + 1].ParameterType
                        match this.Visit elements.[i] with
                        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert && u.Type = typeof<obj> && u.Operand.Type = wanted -> u.Operand
                        | e when e.Type = wanted -> e
                        | e -> Expression.Convert(e, wanted) :> Expression ]
                let siteType = typedefof<CallSite<_>>.MakeGenericType delegateType
                let target = Expression.Field(Expression.Convert(site, siteType), "Target")
                let invoke = Expression.Invoke(target, (Expression.Convert(site, typeof<CallSite>) :> Expression) :: args)
                if node.Method.Name = "InvokeVoid" then invoke :> Expression
                elif invoke.Type = typeof<obj> then invoke :> Expression
                else Expression.Convert(invoke, typeof<obj>) :> Expression
            // A byref site's placeholder (`Binders.ByRefSite`) becomes the typed `Invoke` over a
            // variable per byref parameter (a ref's value in, the default for an out), which LINQ
            // writes back; then the same `obj[]` — result, then each byref's value — the
            // translator unpacks. Required on wasm, where DynamicInvoke does not write byrefs back.
            elif node.Method.DeclaringType = typeof<Binders.ByRefSite> then
                let site = this.Visit node.Arguments.[0]
                let delegateType = (node.Arguments.[1] :?> ConstantExpression).Value :?> Type
                let invokeMethod = delegateType.GetMethod("Invoke")
                let parameters = invokeMethod.GetParameters()
                let elements = (node.Arguments.[2] :?> NewArrayExpression).Expressions
                // Out positions are explicit: in a per-key template an out's value in is a parameter,
                // not the null constant, and an emitted delegate's parameter carries no [Out].
                let outs = (node.Arguments.[4] :?> ConstantExpression).Value :?> int[]
                let unboxed (e: Expression) (wanted: Type) =
                    match e with
                    | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert && u.Type = typeof<obj> && u.Operand.Type = wanted -> u.Operand
                    | e when e.Type = wanted -> e
                    | e -> Expression.Convert(e, wanted) :> Expression
                let temps = ResizeArray<ParameterExpression>()
                let inits = ResizeArray<Expression>()
                let args =
                    [ for i in 0 .. elements.Count - 1 ->
                        let p = parameters.[i + 1]
                        let e = this.Visit elements.[i]
                        if p.ParameterType.IsByRef then
                            let t = p.ParameterType.GetElementType()
                            let temp = Expression.Variable(t, "byRef")
                            temps.Add temp
                            let initial = if Array.contains i outs then Expression.Default(t) :> Expression else unboxed e t
                            inits.Add(Expression.Assign(temp, initial))
                            temp :> Expression
                        else unboxed e p.ParameterType ]
                let siteType = typedefof<CallSite<_>>.MakeGenericType delegateType
                let target = Expression.Field(Expression.Convert(site, siteType), "Target")
                let call = Expression.Call(target, invokeMethod, (Expression.Convert(site, typeof<CallSite>) :> Expression) :: args)
                let result, callStep =
                    if invokeMethod.ReturnType = typeof<Void> then (Expression.Constant(null, typeof<obj>) :> Expression), (call :> Expression)
                    else
                        let r = Expression.Variable(invokeMethod.ReturnType, "result")
                        temps.Add r
                        (Expression.Convert(r, typeof<obj>) :> Expression), (Expression.Assign(r, call) :> Expression)
                let values =
                    Expression.NewArrayInit(typeof<obj>,
                        result :: [ for t in temps do if t.Name = "byRef" then yield (Expression.Convert(t, typeof<obj>) :> Expression) ])
                Expression.Block(typeof<obj[]>, temps, List.ofSeq inits @ [ callStep; values :> Expression ]) :> Expression
            else base.VisitMethodCall node

    let private onWasm =
        string System.Runtime.InteropServices.RuntimeInformation.OSArchitecture = "Wasm"   // no Architecture.Wasm on netstandard2.0

    let private onNetFramework =
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith ".NET Framework"

    /// What every part of the translation of one block needs: where it is (the builder and the
    /// member it sits in) and where its values come from (the state machine struct or the Delay
    /// closure — the same contract: the captured variables are its fields, by name).
    type private Block =
        { BuilderType: Type
          /// Type declaring the enclosing member: the binder's accessibility context, as in C#.
          Context: Type
          /// The enclosing member's whole reflected body, for values the optimizer inlined.
          MemberBody: Expr
          /// The compiler-generated container: the state machine struct, or the Delay closure class.
          ClosureType: Type
          /// The compiled delegate's one parameter: the struct itself (a copy of the machine the
          /// reader is handed by reference), or the closure as `obj`.
          Closure: Var
          /// The container's fields, named after the captured variables.
          Fields: Collections.Generic.IDictionary<string, Reflection.FieldInfo> }
        member this.Self =
            if this.ClosureType.IsValueType then Expr.Var this.Closure
            else Expr.Coerce(Expr.Var this.Closure, this.ClosureType)
        member this.Convert (resultType: Type) (e: Expr) = Binders.convert this.Context resultType e

    /// The builder's members type as `ResumableCode<'D, 'R>` — the shape the compiler's state
    /// machine needs — where the translation produces the plain `'R` value. `codeType` is what
    /// such an expression's type becomes; an expression typed that way that is not a builder call
    /// (the compiler's default value in an unmatched `try … with` arm) is retyped to it so the
    /// rebuilt tree is consistent.
    let private isCode (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Microsoft.FSharp.Core.CompilerServices.ResumableCode<_, _>>
    let private codeType (t: Type) = if isCode t then t.GetGenericArguments().[1] else t
    let private defaultOf (t: Type) =
        if t = typeof<unit> then Expr.Value(())
        elif t.IsValueType then Expr.DefaultValue t
        else Expr.Value(null, t)

    /// The generic rewriter, threaded through the sections so each can rewrite a subexpression:
    /// the variables bound inside the expression so far, and the expression.
    type private Rewrite = Set<Var> -> Expr -> Expr

    /// A `unit` expression may compile to a void call, which cannot be the value of a lambda or
    /// of the block; end it with the unit constant so the tree has a `Unit` value.
    let private asUnit (e: Expr) =
        if e.Type = typeof<unit> then Expr.Sequential(e, Expr.Value(())) else e

    /// A `unit`-typed call or application — possibly a void method, which the converter cannot
    /// use as a value — as opposed to `()` or a unit-typed variable.
    let private isUnitCall (e: Expr) =
        e.Type = typeof<unit> && (match e with Call _ | Application _ -> true | _ -> false)

    /// On Mono's browser-wasm runtime a nested lambda that captures nothing loses its arguments
    /// when invoked (see DlrRuntime); one that captures is fine. So there, a nested delegate
    /// body is made to capture the block's closure parameter, which costs nothing elsewhere.
    let private capturing (block: Block) (body: Expr) =
        if onWasm then Expr.Let(Var("captured", block.Closure.Type), Expr.Var block.Closure, body) else body

    /// Where a free variable of the body comes from: the Delay closure, or failing that the
    /// enclosing member's body.
    module private Captures =

        /// The container's fields by name. A state machine also has fields of its own, `Data`
        /// (a `DlrData<_>`) and `ResumptionPoint` (an int), declared first, and they are skipped
        /// rather than found by name: a captured `Data` gets a second field of that name after
        /// them (IL allows it, the types differ); a captured `ResumptionPoint` is an `FSharpRef`
        /// when mutable, a compiler error (FS2014, as in `task { }`) when an `int` the optimizer
        /// kept, and when it inlined the value there is no field, and the body's variable of that
        /// name must resolve from the enclosing member, not to the machine's own counter.
        let fields (containerType: Type) : Collections.Generic.IDictionary<string, Reflection.FieldInfo> =
            let all = containerType.GetFields(Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic)
            let isData (f: Reflection.FieldInfo) =
                f.Name = "Data" && f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() = typedefof<DlrData<_>>
            let own =
                if containerType.IsValueType then
                    [ yield! all |> Array.filter isData
                      yield! all |> Array.filter (fun f -> f.Name = "ResumptionPoint" && f.FieldType = typeof<int>) |> Array.truncate 1 ]
                else []
            let fields = Collections.Generic.Dictionary<string, Reflection.FieldInfo>()
            for f in all do
                if not (List.contains f own) then fields.[f.Name] <- f
            fields :> _

        let private isRefCell (f: Reflection.FieldInfo) (t: Type) =
            f.FieldType.IsGenericType
            && f.FieldType.GetGenericTypeDefinition() = typedefof<Ref<_>>
            && f.FieldType.GetGenericArguments().[0] = t

        /// A free variable of the body becomes a read of the closure field of the same name.
        /// A captured `let mutable` is stored as an FSharpRef cell; read through it. When there is
        /// no such field the optimizer inlined the variable's definition (a literal, a local
        /// function, ...) instead of capturing it, so substitute that definition from the
        /// enclosing member's body; its own free variables resolve the same way.
        let read (block: Block) (resolve: Expr -> Expr) (v: Var) : Expr =
            match block.Fields.TryGetValue v.Name with
            | true, f when f.FieldType = v.Type -> Expr.FieldGet(block.Self, f)
            | true, f when isRefCell f v.Type ->
                Expr.PropertyGet(Expr.FieldGet(block.Self, f), f.FieldType.GetProperty("Value"))
            | true, f ->
                raise (DlrTranslationException(
                        sprintf "dlr { } captured '%s' as %s but the body uses it as %s." v.Name f.FieldType.Name v.Type.Name))
            | _ ->
                match letDefinition v block.MemberBody with
                | Some def -> resolve def
                | None ->
                    match parameterArgument v block.MemberBody with
                    | Some arg -> resolve arg
                    | None ->
                        raise (DlrTranslationException(
                                sprintf "dlr { } could not find captured variable '%s' on closure %s (fields: %s), a let binding for it, or a single application supplying it (the optimizer inlined it; give the enclosing local function more than one call site or hoist the block)."
                                    v.Name block.ClosureType.Name (String.Join(", ", block.Fields.Keys))))

        /// `v <- value` on a captured `let mutable`: a write through its FSharpRef cell.
        let assign (block: Block) (v: Var) (value: Expr) : Expr =
            match block.Fields.TryGetValue v.Name with
            | true, f when isRefCell f v.Type ->
                Expr.PropertySet(Expr.FieldGet(block.Self, f), f.FieldType.GetProperty("Value"), value)
            | _ ->
                raise (DlrTranslationException(
                        sprintf "dlr { } assigns '%s', which is not a captured mutable on closure %s (fields: %s)."
                            v.Name block.ClosureType.Name (String.Join(", ", block.Fields.Keys))))

    /// The builder's own methods: `Return`/`Zero`/`Combine` fold away, and the control-flow
    /// members become `DlrRuntime` calls over delegates.
    module private Plumbing =

        /// A body as a `Func<..>` delegate over `vars` (see DlrRuntime for why not an F# function).
        /// The delegate's type is built from the variables' types and the body's; a `unit` body
        /// (which may compile to a void call) becomes a `Func<.., unit>`, not an `Action`.
        let private func (block: Block) (rewriteIn: Rewrite) (bound: Set<Var>) (vars: Var list) (body: Expr) : Expr =
            let bound = vars |> List.fold (fun b v -> Set.add v b) bound
            let body = capturing block (asUnit (rewriteIn bound body))
            let delegateType = Expression.GetFuncType(Array.append (vars |> List.map (fun v -> v.Type) |> Array.ofList) [| body.Type |])
            Expr.NewDelegate(delegateType, vars, body)

        /// A call on the builder (Discover already unwrapped the outermost Delay).
        let call (block: Block) (rewriteIn: Rewrite) (bound: Set<Var>) (mi: Reflection.MethodInfo) (args: Expr list) (e: Expr) : Expr =
            let rewrite = rewriteIn bound
            let func = func block rewriteIn bound
            match mi.Name, args with
            | "Return", [ value ] -> rewrite value
            | "Zero", [] -> Expr.Value(())
            | "Combine", [ first; Call(_, d, [ Lambda(_, rest) ]) ] when d.Name = "Delay" ->
                Expr.Sequential(rewrite first, rewrite rest)
            | "For", [ items; Lambda(x, body) ] ->
                let items = rewrite items
                Expr.Call(forEach.MakeGenericMethod(x.Type), [ items; func [ x ] body ])
            | "While", [ Lambda(_, guard); Call(_, d, [ Lambda(_, body) ]) ] when d.Name = "Delay" ->
                Expr.Call(whileLoop, [ func [] guard; func [] body ])
            | "TryWith", [ Call(_, d, [ Lambda(_, body) ]); Lambda(ex, handler) ] when d.Name = "Delay" ->
                Expr.Call(tryWith.MakeGenericMethod(codeType e.Type), [ func [] body; func [ ex ] handler ])
            | "TryFinally", [ Call(_, d, [ Lambda(_, body) ]); Lambda(_, compensation) ] when d.Name = "Delay" ->
                Expr.Call(tryFinally.MakeGenericMethod(codeType e.Type), [ func [] body; func [] compensation ])
            | "Using", [ resource; Lambda(r, body) ] ->
                Expr.Call(using.MakeGenericMethod(r.Type, codeType e.Type), [ rewrite resource; func [ r ] body ])
            // A nested dlr { } is compiled as part of this one: at run time its closure would be
            // made by our compiled code, not the F# compiler, so it has no reflected body of its own.
            | "Run", [ Call(_, d, [ Lambda(_, inner) ]); _; _ ] when d.Name = "Delay" -> rewrite inner
            | name, _ -> unsupported (sprintf "the '%s' construct" name) e

    /// The marker operations: a literal name is a baked site; a computed name or runtime type
    /// arguments bind per key through a SiteCache.
    module private Members =

        let private targetArg (rewriteIn: Rewrite) bound (target: Expr) =
            match target with
            | StaticTarget t -> Binders.staticTarget t
            | _ ->
                let t = rewriteIn bound target
                Binders.dynamicArg (if t.Type = typeof<obj> then t else Expr.Coerce(t, typeof<obj>))

        let private valueArg (rewriteIn: Rewrite) bound (value: Expr) =
            match value with
            | Value _ -> Binders.constant (Binders.typedArg value)
            // A unit-valued expression (a void call, `ignore x`) as a value: run, then `()`.
            | _ when value.Type = typeof<unit> -> Binders.typedArg (Expr.Sequential(rewriteIn bound value, Expr.Value(())))
            | _ -> Binders.typedArg (rewriteIn bound value)

        let private argList (rewriteIn: Rewrite) bound (argExprs: Expr list) =
            let bindings = ResizeArray()
            let args =
                [ for a in argExprs do
                    match a with
                    | TypeArgs _ -> unsupported "Dlr.typeArgs anywhere but as the first argument of a member call (a value invoked with Dlr.call / Dlr.apply or a constructor takes no type arguments)" a
                    | NamedOf _ | ArgsOf _ -> unsupported "Dlr.namedOf / Dlr.argsOf here (they go in the arguments of a member call, Dlr.call / Dlr.apply, or Dlr.new')" a
                    | OutMarker | RefMarker _ -> unsupported "Dlr.out / Dlr.ref here: so far they go in the arguments of a member call (x?M(…), Dlr.get, Dlr.invoke) with a literal name" a
                    | NamedRecord(lets, fields) ->
                        bindings.AddRange lets
                        let inner = bindings |> Seq.fold (fun (b: Set<Var>) (v, _) -> b.Add v) bound
                        for (name, v) in fields -> Binders.named name (valueArg rewriteIn inner v)
                    // Argument positions are generic-typed, so a `Coerce(_, obj)` here is the user's
                    // `x :> obj` and means what `box x` means: an obj argument, runtime dispatch.
                    | v -> yield valueArg rewriteIn bound v ]
            List.ofSeq bindings, args

        let private bind (rewriteIn: Rewrite) bound (bindings: (Var * Expr) list) (call: Expr) =
            List.foldBack (fun (v, value) body -> Expr.Let(v, rewriteIn bound value, body)) bindings call

        let private indexList (rewriteIn: Rewrite) bound (indexes: Expr list) = [ for i in indexes -> valueArg rewriteIn bound i ]

        let private finish (block: Block) discard (resultType: Type) (call: Expr) =
            if discard then call else block.Convert resultType call

        /// A member call with `Dlr.out` / `Dlr.ref` arguments (#131). The outs come back as F# returns
        /// a method's out parameters: the result is a tuple of the return value then each out in
        /// order, or the outs alone for a void method (the bare value for one); each out's type is
        /// its element of the result type. A ref's value goes in from its `let mutable` and the
        /// method's write is assigned back. Through `Binders.ByRefSite`, whose `obj[]` is unpacked here.
        let private byRefCall (rewriteIn: Rewrite) bound (argExprs: Expr list) (resultType: Type) (convertReturn: Type -> Expr -> Expr) (makeCall: bool -> Binders.Arg list -> Expr) : Expr =
            let outCount = argExprs |> List.filter (function OutMarker -> true | _ -> false) |> List.length
            let shapeError () =
                unsupported (sprintf "a call with %d Dlr.out argument(s) whose result is %s: the result is the return value then each out as a tuple, the outs alone as a tuple for a void method, or the one out's value" outCount resultType.Name) (Expr.Value resultType.Name)
            let returnType, outTypes =
                if outCount = 0 then (if resultType = typeof<unit> then None else Some resultType), []
                elif resultType = typeof<unit> then shapeError ()
                elif FSharpType.IsTuple resultType && not resultType.IsValueType then
                    let elements = FSharpType.GetTupleElements resultType
                    if elements.Length = outCount + 1 then Some elements.[0], List.ofArray elements.[1..]
                    elif elements.Length = outCount then None, List.ofArray elements
                    else shapeError ()
                elif outCount = 1 then None, [ resultType ]
                else shapeError ()
            let pendingOuts = System.Collections.Generic.Queue<Type>(outTypes)
            // Per byref argument, in order: an out's type, or a ref's place (its type and how to
            // write it back).
            let byRefs = ResizeArray<Choice<Type, Type * (Expr -> Expr)>>()
            let namedBindings = ResizeArray<Var * Expr>()
            let args =
                [ for a in argExprs do
                    match a with
                    | OutMarker ->
                        let t = pendingOuts.Dequeue()
                        byRefs.Add(Choice1Of2 t)
                        yield Binders.byRefArg true t (Expr.Value(null, typeof<obj>))
                    | RefMarker(Var v) when v.IsMutable ->
                        byRefs.Add(Choice2Of2(v.Type, fun value -> Expr.VarSet(v, value)))
                        yield Binders.byRefArg false v.Type (rewriteIn bound (Expr.Var v))
                    // A `let mutable` a closure captures is a ref cell by the time it is quoted.
                    | RefMarker(PropertyGet(Some(Var cell as cellExpr), p, [])) when cell.Type.IsGenericType && cell.Type.GetGenericTypeDefinition() = typedefof<Ref<_>> ->
                        byRefs.Add(Choice2Of2(p.PropertyType, fun value -> Expr.PropertySet(cellExpr, p, value)))
                        yield Binders.byRefArg false p.PropertyType (rewriteIn bound (Expr.PropertyGet(cellExpr, p)))
                    | RefMarker other -> unsupported "Dlr.ref of anything but a let mutable (its value goes in, and the method's write is assigned back to it)" other
                    // Named arguments, as `argList` takes them: the record's field temporaries
                    // wrap the call, in source order.
                    | NamedRecord(lets, fields) ->
                        namedBindings.AddRange lets
                        let inner = namedBindings |> Seq.fold (fun (b: Set<Var>) (v, _) -> b.Add v) bound
                        for (name, v) in fields -> Binders.named name (valueArg rewriteIn inner v)
                    | NamedOf _ | ArgsOf _ -> unsupported "Dlr.namedOf / Dlr.argsOf in a call with Dlr.out or Dlr.ref (not supported: the outs' types are fixed by the result, the splat's arity is not)" a
                    | TypeArgs _ -> unsupported "Dlr.typeArgs anywhere but as the first argument of a member call" a
                    | v -> yield valueArg rewriteIn bound v ]
            let call = makeCall returnType.IsNone args
            let results = Var("byRefResults", typeof<obj[]>)
            let at (i: int) = Expr.Call(arrayGet.MakeGenericMethod typeof<obj>, [ Expr.Var results; Expr.Value i ])
            let unboxAt (t: Type) (i: int) = Expr.Call(unboxTo.MakeGenericMethod t, [ at i ])
            let bound' = Set.add results bound
            let writeBacks =
                [ for i, b in Seq.indexed byRefs do
                    match b with
                    | Choice2Of2(t, write) -> yield rewriteIn bound' (write (unboxAt t (i + 1)))
                    | Choice1Of2 _ -> () ]
            let outValues = [ for i, b in Seq.indexed byRefs do match b with Choice1Of2 t -> yield unboxAt t (i + 1) | Choice2Of2 _ -> () ]
            let value =
                match returnType, outValues with
                | None, [] -> Expr.Value(())
                | Some t, [] -> convertReturn t (at 0)
                | Some t, outs -> Expr.NewTuple(convertReturn t (at 0) :: outs)
                | None, [ single ] -> single
                | None, outs -> Expr.NewTuple outs
            let body = List.foldBack (fun w rest -> Expr.Sequential(w, rest)) writeBacks value
            List.foldBack (fun (v, value) body -> Expr.Let(v, rewriteIn bound value, body)) (List.ofSeq namedBindings) (Expr.Let(results, call, body))

        /// The tuple bindings of `splitArgs`, added to the bound set for the argument rewrites.
        let private withTuple (bound: Set<Var>) (tupleBindings: (Var * Expr) list) =
            tupleBindings |> List.fold (fun (b: Set<Var>) (v, _) -> b.Add v) bound

        /// The name and the type arguments of a keyed site, each either static (a constant in the
        /// key) or an expression evaluated per call in the scope the site is emitted in.
        type KeySpec = Choice<string, Expr> * Choice<Type list, Expr>

        /// A delegate over `parameters` returning obj, compiled once per site: a `Func<…>` while
        /// its arity allows; past that the delegate type would be emitted at run time, which a
        /// quotation must not name (see `Binders.WideSite`), so it is a `Func<obj[], obj>` over the
        /// parameters packed (`packArguments` at the call) and unpacked to their types inside.
        let private isWide (parameters: Var list) = parameters.Length + 1 > 17   // Func's 17 type parameters, the result among them
        let private delegateTypeOver (parameters: Var list) =
            if isWide parameters then typeof<Func<obj[], obj>>
            else Expression.GetDelegateType(Array.ofList ([ for v in parameters -> v.Type ] @ [ typeof<obj> ]))
        let private lambdaOver (parameters: Var list) (body: Expr) =
            if not (isWide parameters) then Expr.NewDelegate(delegateTypeOver parameters, parameters, body)
            else
                let packed = Var("packed", typeof<obj[]>)
                let atArg = typeof<NamedOfCache>.GetMethod("At")
                let unpacked =
                    List.foldBack (fun (i, v: Var) (inner: Expr) ->
                        let element = Expr.Call(atArg, [ Expr.Var packed; Expr.Value i ])
                        Expr.Let(v, (if v.Type = typeof<obj> then element else Expr.Coerce(element, v.Type)), inner))
                        (List.indexed parameters) body
                Expr.NewDelegate(typeof<Func<obj[], obj>>, [ packed ], unpacked)
        let private packArguments (parameters: Var list) (arguments: Expr list) =
            if not (isWide parameters) then arguments
            else [ Expr.NewArray(typeof<obj>, [ for a in arguments -> if a.Type = typeof<obj> then a else Expr.Coerce(a, typeof<obj>) ]) ]

        /// An operation whose binder inputs — the member name, the type arguments, or both — are
        /// only known at run time: the operation's delegate is compiled once here with its call
        /// sites as parameters (lifted from a template built for a placeholder key), and a
        /// SiteCache constant creates the sites per distinct `(name, types)` key; the emitted
        /// code is `let sites = cache.Get((name, types)) in delegate.Invoke(sites.[0], …, target, args…)`.
        /// Argument names in `Dlr.named` stay static. This is the core over prepared arguments;
        /// the name and type expressions must be valid where the result is placed.
        let private keyedSiteCore (block: Block) (key: KeySpec) (targetInfo: Binders.Arg) (argInfos: Binders.Arg list) (resultType: Type) (site: string -> Type list -> Binders.Arg -> Binders.Arg list -> Expr) : Expr =
            let targetVar = Var("target", targetInfo.Type)
            // A byref argument (`Dlr.out` / `Dlr.ref`) is passed as its value: a quotation variable cannot be a byref.
            let argVars = argInfos |> List.mapi (fun i a -> Var(sprintf "a%d" i, (if a.Type.IsByRef then a.Expr.Type else a.Type)))
            let template (name: string, types: Type list) =
                let args = List.map2 (fun (info: Binders.Arg) (v: Var) -> { info with Expr = Expr.Var v }) argInfos argVars
                site name types { targetInfo with Expr = Expr.Var targetVar } args
            // The operation's shape does not depend on the key, only its sites do: build it once
            // for a placeholder, lift every site constant into a parameter, and compile that one
            // delegate now. Per key, the cache creates the sites and hands them back in the
            // same order.
            let placeholderName, nameE =
                match fst key with
                | Choice1Of2 name -> name, Expr.Value name
                | Choice2Of2 e -> "name", e
            let placeholderTypes, typesE =
                match snd key with
                | Choice1Of2 ts -> ts, Expr.Value(ts, typeof<Type list>)
                | Choice2Of2 e -> [], e
            let placeholder = template (placeholderName, placeholderTypes)
            let sites = SiteCache<string * Type list>.Sites placeholder
            // A wide site's type names an emitted delegate type: keep it out of the quotation (see
            // `Binders.WideSite`) by typing the parameter as the base `CallSite`.
            let siteVars = sites |> List.mapi (fun i s -> s, Var(sprintf "site%d" i, (let t = s.GetType() in if t.GetGenericArguments().[0].Assembly.IsDynamic then typeof<CallSite> else t)))
            let rec lift (e: Expr) =
                match e with
                | Value(v, _) when (v :? CallSite) ->
                    match siteVars |> List.tryFind (fun (s, _) -> obj.ReferenceEquals(s, v)) with
                    | Some(_, var) -> Expr.Var var
                    | None -> e
                | ShapeVar _ -> e
                | ShapeLambda(v, body) -> Expr.Lambda(v, lift body)
                | ShapeCombination(shape, args) -> RebuildShapeCombination(shape, List.map lift args)
            let body = lift placeholder
            // A discarded result is a void site: the delegate still returns obj, so hand back null.
            let boxed =
                if body.Type = typeof<obj> then body
                elif body.Type = typeof<unit> || body.Type = typeof<Void> then Expr.Sequential(body, Expr.Value(null, typeof<obj>))
                else Expr.Coerce(body, typeof<obj>)
            let parameters = [ for _, v in siteVars -> v ] @ targetVar :: argVars
            let cache = SiteCache<string * Type list>(template)
            let cacheType = typeof<SiteCache<string * Type list>>
            let sitesVar = Var("sites", typeof<CallSite[]>)
            let at = cacheType.GetMethod("At")
            let siteArgs = siteVars |> List.mapi (fun i (_, v) -> Expr.Coerce(Expr.Call(at, [ Expr.Var sitesVar; Expr.Value i ]), v.Type))
            let arguments = siteArgs @ targetInfo.Expr :: [ for a in argInfos -> a.Expr ]
            let delegateType = delegateTypeOver parameters
            // Through the hoister like every compiled tree: a wide site's placeholder in the template
            // is rewritten there (its site arrives as a parameter, which the rewrite converts).
            let linq = LeafExpressionConverter.QuotationToExpression (lambdaOver parameters boxed) :?> LambdaExpression
            let compiled = (SiteHoister().Visit linq :?> LambdaExpression).Compile()
            let invocation = Expr.Call(Expr.Value(compiled, delegateType), delegateType.GetMethod("Invoke"), packArguments parameters arguments)
            let call = Expr.Let(sitesVar, Expr.Call(Expr.Value(cache, cacheType), cacheType.GetMethod("Get"), [ Expr.NewTuple [ nameE; typesE ] ]), invocation)
            // A byref site's `obj[]` (result and byref values) is passed through for its caller to unpack.
            if FSharpType.IsFunction resultType || resultType = typeof<obj[]> then Expr.Coerce(call, resultType) else block.Convert resultType call

        /// The key of a computed name / run-time type arguments, its expressions rewritten in
        /// the block's scope.
        let private keySpec (rewriteIn: Rewrite) bound (nameExpr: Expr) (typeArgs: TypeArgsSpec) : KeySpec =
            (match nameExpr with Literal name -> Choice1Of2(string name) | e -> Choice2Of2(rewriteIn bound e)),
            (match typeArgs with StaticTypes ts -> Choice1Of2 ts | RuntimeTypes e -> Choice2Of2(rewriteIn bound e))

        /// `keyedSiteCore` over argument expressions, in the block's scope.
        let private keyedSite (block: Block) (rewriteIn: Rewrite) bound (nameExpr: Expr) (typeArgs: TypeArgsSpec) (target: Expr) (argExprs: Expr list) (resultType: Type) (site: string -> Type list -> Binders.Arg -> Binders.Arg list -> Expr) : Expr =
            let bindings, argInfos = argList rewriteIn bound argExprs
            keyedSiteCore block (keySpec rewriteIn bound nameExpr typeArgs) (targetArg rewriteIn bound target) argInfos resultType site
            |> bind rewriteIn bound bindings

        /// An invocation whose arguments include `Dlr.namedOf pairs`: the operation is compiled
        /// per distinct name list — the names decide the site's arity, so the whole delegate is
        /// per key — through a bounded `NamedOfCache` constant, taking the target, the fixed
        /// arguments and the named values (`obj`, dispatched on runtime type). `operation` builds
        /// the site expression for a target and its full argument list. `None` when there is no
        /// `namedOf` in `argExprs`. With a computed member name / run-time type arguments
        /// (`key`), their expressions are evaluated in the block's scope and passed into the
        /// per-name-list delegate as parameters, where `operation` gets them for a `keyedSiteCore`.
        let private namedOfCallKeyed (block: Block) (rewriteIn: Rewrite) bound (target: Expr) (argExprs: Expr list) (resultType: Type) (discard: bool) (key: KeySpec option) (operation: KeySpec -> Binders.Arg -> Binders.Arg list -> Expr) : Expr option =
            let namedOfs = argExprs |> List.choose (function NamedOf pairs -> Some pairs | _ -> None)
            let argsOfs = argExprs |> List.choose (function ArgsOf values -> Some values | _ -> None)
            if namedOfs.IsEmpty && argsOfs.IsEmpty then None
            else
                if namedOfs.Length > 1 then unsupported "more than one Dlr.namedOf in one call (concatenate the lists)" namedOfs.[1]
                if argsOfs.Length > 1 then unsupported "more than one Dlr.argsOf in one call (concatenate the lists)" argsOfs.[1]
                // Named arguments are the call's trailing ones (the binder's CallInfo names the
                // last arguments), so nothing positional may follow Dlr.namedOf.
                (match argExprs |> List.tryFindIndex (function NamedOf _ -> true | _ -> false) with
                 | Some i when argExprs |> List.skip (i + 1) |> List.exists (function NamedRecord _ -> false | _ -> true) ->
                    unsupported "a positional argument after Dlr.namedOf (named arguments come last)" argExprs.[i]
                 | _ -> ())
                let pairsExpr = match namedOfs with [ p ] -> p | _ -> Expr.Value(([]: (string * obj) list), typeof<(string * obj) list>)
                let positionalExpr = match argsOfs with [ p ] -> p | _ -> Expr.Value(([]: obj list), typeof<obj list>)
                /// The argument list's layout: fixed arguments by index, the positional splat, the
                /// named splat — so the compiled call keeps the source order.
                let layout =
                    let mutable next = 0
                    argExprs |> List.choose (fun a ->
                        match a with
                        | NamedOf _ -> Some(Choice3Of3 ())
                        | ArgsOf _ -> Some(Choice2Of3 ())
                        | _ -> (let i = next in next <- next + 1; Some(Choice1Of3 i)))
                let fixedExprs = argExprs |> List.filter (fun a -> not (isSplat a))
                let bindings, fixedInfos = argList rewriteIn bound fixedExprs
                let targetInfo = targetArg rewriteIn bound target
                let targetVar = Var("target", targetInfo.Type)
                let fixedVars = fixedInfos |> List.mapi (fun i a -> Var(sprintf "a%d" i, a.Type))
                let valuesVar = Var("values", typeof<obj[]>)
                let at = typeof<NamedOfCache>.GetMethod("At")
                // A computed name / run-time type list are evaluated out here and passed in as
                // parameters: the delegate does not see the block's scope.
                let keyVars, keyExprs, innerKey =
                    match key with
                    | None -> [], [], None
                    | Some(name, types) ->
                        let nameVar, nameE = (match name with Choice1Of2 n -> None, Choice1Of2 n | Choice2Of2 e -> (let v = Var("name", typeof<string>) in Some(v, e), Choice2Of2(Expr.Var v)))
                        let typesVar, typesE = (match types with Choice1Of2 ts -> None, Choice1Of2 ts | Choice2Of2 e -> (let v = Var("types", typeof<Type list>) in Some(v, e), Choice2Of2(Expr.Var v)))
                        let vars = [ nameVar; typesVar ] |> List.choose id
                        List.map fst vars, List.map snd vars, Some((nameE, typesE): KeySpec)
                // One delegate type for every key at this site: the names change the sites inside,
                // not the parameters, so the call is a typed Invoke, not DynamicInvoke.
                let parameters = targetVar :: fixedVars @ keyVars @ [ valuesVar ]
                let delegateType = delegateTypeOver parameters
                let compile (names: string list) : Delegate =
                    let fixed' = List.map2 (fun (info: Binders.Arg) (v: Var) -> { info with Expr = Expr.Var v }) fixedInfos fixedVars
                    // `fixedInfos` are grouped per source argument (a Dlr.named record is several):
                    // walk the fixed expressions again to know how many each contributed.
                    let fixedGroups =
                        let counted = fixedExprs |> List.map (fun a -> match a with NamedRecord(_, fields) -> fields.Length | _ -> 1)
                        let mutable offset = 0
                        [ for n in counted -> (let g = List.take n (List.skip offset fixed') in offset <- offset + n; g) ]
                    let value i = Binders.dynamicArg (Expr.Call(at, [ Expr.Var valuesVar; Expr.Value i ]))
                    let positionalCount = names |> List.takeWhile (fun n -> n.Length = 0) |> List.length
                    let positional = [ for i in 0 .. positionalCount - 1 -> value i ]
                    let named = names |> List.skip positionalCount |> List.mapi (fun j name -> Binders.named name (value (positionalCount + j)))
                    let args =
                        layout |> List.collect (function
                            | Choice1Of3 i -> fixedGroups.[i]
                            | Choice2Of3 () -> positional
                            | Choice3Of3 () -> named)
                    let body = operation (defaultArg innerKey (Choice1Of2 "", Choice1Of2 [])) { targetInfo with Expr = Expr.Var targetVar } args
                    let boxed =
                        if body.Type = typeof<obj> then body
                        elif body.Type = typeof<unit> || body.Type = typeof<Void> then Expr.Sequential(body, Expr.Value(null, typeof<obj>))
                        else Expr.Coerce(body, typeof<obj>)
                    let linq = LeafExpressionConverter.QuotationToExpression (lambdaOver parameters boxed) :?> LambdaExpression
                    (SiteHoister().Visit linq :?> LambdaExpression).Compile()
                let cache = NamedOfCache(compile)
                let cacheType = typeof<NamedOfCache>
                let pairsVar = Var("pairs", typeof<(string * obj) list>)
                let positionalVar = Var("positional", typeof<obj list>)
                let delegateVar = Var("d", delegateType)
                let values = Expr.Call(cacheType.GetMethod("Values"), [ Expr.Var positionalVar; Expr.Var pairsVar ])
                let call =
                    Expr.Let(positionalVar, rewriteIn bound positionalExpr,
                      Expr.Let(pairsVar, rewriteIn bound pairsExpr,
                        Expr.Let(delegateVar, Expr.Coerce(Expr.Call(Expr.Value(cache, cacheType), cacheType.GetMethod("Get"), [ Expr.Var positionalVar; Expr.Var pairsVar ]), delegateType),
                            Expr.Call(Expr.Var delegateVar, delegateType.GetMethod("Invoke"), packArguments parameters (targetInfo.Expr :: [ for a in fixedInfos -> a.Expr ] @ keyExprs @ [ values ])))))
                (if discard then Expr.Sequential(call, Expr.Value(())) else block.Convert resultType call)
                |> bind rewriteIn bound bindings
                |> Some

        /// An invocation whose arguments include `Dlr.namedOf pairs`, with a literal member name.
        let private namedOfCall (block: Block) (rewriteIn: Rewrite) bound (target: Expr) (argExprs: Expr list) (resultType: Type) (discard: bool) (operation: Binders.Arg -> Binders.Arg list -> Expr) : Expr option =
            namedOfCallKeyed block rewriteIn bound target argExprs resultType discard None (fun _ t args -> operation t args)

        /// `(?) x name` with a computed name (no type arguments): see keyedSite.
        let private computedName block rewriteIn bound (nameExpr: Expr) (target: Expr) (argExprs: Expr list) (resultType: Type) (site: string -> Binders.Arg -> Binders.Arg list -> Expr) : Expr =
            keyedSite block rewriteIn bound nameExpr (StaticTypes []) target argExprs resultType (fun name _ targetArg args -> site name targetArg args)

        /// `Dlr.addAssign`/`subtractAssign`: bind the target and value once, then both branches
        /// of the IsEvent decision refer to them. A computed name goes through the SiteCache
        /// like any other member operation.
        let private compoundAssign (block: Block) (rewriteIn: Rewrite) bound (subtract: bool) (nameExpr: Expr) (target: Expr) (value: Expr) : Expr =
            match nameExpr with
            | Literal name ->
                let targetInfo = targetArg rewriteIn bound target
                let v = rewriteIn bound value
                let tv = Var("target", targetInfo.Type)
                let vv = Var("value", v.Type)
                // A literal value keeps C#'s constant conversions (a byte member += 1) even though
                // it is read through a variable here.
                let valueArg =
                    let a = Binders.typedArg (Expr.Var vv)
                    match value with
                    | Value _ -> Binders.constant a
                    | _ -> a
                let body = Binders.compoundAssign block.Context (string name) subtract { targetInfo with Expr = Expr.Var tv } valueArg
                Expr.Let(tv, targetInfo.Expr, Expr.Let(vv, v, body))
            | _ ->
                computedName block rewriteIn bound nameExpr target [ value ] typeof<unit> (fun name targetArg args ->
                    // The name-cache template already makes target and value delegate parameters.
                    Expr.Sequential(Binders.compoundAssign block.Context name subtract targetArg (List.head args), Expr.Value(null, typeof<obj>)))

        /// The expression, if it is a marker operation.
        let tryOperation (block: Block) (rewriteIn: Rewrite) (bound: Set<Var>) (e: Expr) : Expr option =
            let context = block.Context
            let convert = block.Convert
            let rewrite = rewriteIn bound
            let targetArg = targetArg rewriteIn
            let valueArg = valueArg rewriteIn
            let argList = argList rewriteIn
            let bind = bind rewriteIn
            let indexList = indexList rewriteIn
            let finish = finish block
            let computedName = computedName block rewriteIn bound
            match e with
            | MemberOp(InvokeMember(target, nameExpr, argExpr)) when (snd (splitArgs argExpr)) |> List.exists isByRefMarker ->
                let tupleBindings, argExprs = splitArgs argExpr
                if not tupleBindings.IsEmpty then unsupported "Dlr.out / Dlr.ref in a tuple held in a variable" argExpr
                let typeArgs, argExprs =
                    match argExprs with
                    | TypeArgs spec :: rest -> spec, rest
                    | args -> StaticTypes [], args
                let targetInfo = targetArg bound target
                let makeCall =
                    match nameExpr, typeArgs with
                    | Literal name, StaticTypes ts -> fun discard args -> Binders.invokeMemberByRef context (string name) ts discard (targetInfo :: args)
                    // A computed name or run-time type arguments: a site per key, as for any member call.
                    | _ ->
                        let key = keySpec rewriteIn bound nameExpr typeArgs
                        fun discard args ->
                            keyedSiteCore block key targetInfo args typeof<obj[]> (fun name ts t a -> Binders.invokeMemberByRef context name ts discard (t :: a))
                Some(byRefCall rewriteIn bound argExprs e.Type convert makeCall)
            | MemberOp(InvokeMember(target, nameExpr, argExpr)) ->
                let tupleBindings, argExprs = splitArgs argExpr
                let typeArgs, argExprs =
                    match argExprs with
                    | TypeArgs spec :: rest -> spec, rest
                    | args -> StaticTypes [], args
                // A computed name / type list is evaluated by the keyed site ahead of the call,
                // so it too is sequenced when impure.
                let keys = [ (match nameExpr with Literal _ -> None | k -> Some k); (match typeArgs with RuntimeTypes k -> Some k | _ -> None) ] |> List.choose id
                let ordered = hoists tupleBindings argExprs || keys |> List.exists (fun k -> not (isPure k))
                let bindings, bound', target, nameExpr, typeArgs, argExprs =
                    if ordered then
                        let bindings, vars, target', keys', argExprs' = sequenced (Some target) keys tupleBindings argExprs
                        let nameExpr', keys' = (match nameExpr with Literal _ -> nameExpr, keys' | _ -> List.head keys', List.tail keys')
                        let typeArgs' = (match typeArgs with RuntimeTypes _ -> RuntimeTypes(List.head keys') | t -> t)
                        bindings, Set.union bound vars, target'.Value, nameExpr', typeArgs', argExprs'
                    else tupleBindings, withTuple bound tupleBindings, target, nameExpr, typeArgs, argExprs
                let discard = e.Type = typeof<unit>
                let hasNamedOf = argExprs |> List.exists isSplat
                match nameExpr, typeArgs with
                | Literal name, StaticTypes ts when hasNamedOf ->
                    (namedOfCall block rewriteIn bound' target argExprs e.Type discard (fun t args -> Binders.invokeMemberOrApply context (string name) ts discard t args)).Value
                | Literal name, StaticTypes ts ->
                    let argBindings, args = argList bound' argExprs
                    Binders.invokeMemberOrApply context (string name) ts discard (targetArg bound' target) args |> finish discard e.Type |> bind bound' argBindings
                | _ when hasNamedOf ->
                    (namedOfCallKeyed block rewriteIn bound' target argExprs e.Type discard (Some(keySpec rewriteIn bound' nameExpr typeArgs)) (fun key t args ->
                        keyedSiteCore block key t args typeof<obj> (fun name ts targetArg args -> Binders.invokeMemberOrApply context name ts discard targetArg args))).Value
                | _ ->
                    keyedSite block rewriteIn bound' nameExpr typeArgs target argExprs e.Type (fun name ts targetArg args ->
                        Binders.invokeMemberOrApply context name ts discard targetArg args)
                |> bind bound bindings
                |> Some
            | MemberOp(GetMember(target, nameExpr)) when FSharpType.IsFunction e.Type ->
                // Read as an F# function: a curried invoker of the member (method, delegate or F#
                // function), so `let f: int -> int -> int = dlr { return x?Add }` then `f 1 2`.
                match nameExpr with
                | Literal name -> Binders.functionMember context (string name) e.Type (targetArg bound target)
                | _ -> computedName nameExpr target [] e.Type (fun name targetArg _ -> Binders.functionMember context name e.Type targetArg)
                |> Some
            | MemberOp(GetMember(target, nameExpr)) ->
                match nameExpr with
                | Literal name -> Binders.getMember context (string name) (targetArg bound target) |> convert e.Type
                | _ -> computedName nameExpr target [] e.Type (fun name targetArg _ -> Binders.getMember context name targetArg)
                |> Some
            | Op opAddAssign [ nameExpr; Unboxed value; Unboxed target ] -> Some(compoundAssign block rewriteIn bound false nameExpr target value)
            | Op opSubtractAssign [ nameExpr; Unboxed value; Unboxed target ] -> Some(compoundAssign block rewriteIn bound true nameExpr target value)
            | MemberOp(SetMember(target, nameExpr, value)) ->
                match nameExpr with
                | Literal name -> Binders.setMember context (string name) (targetArg bound target) (valueArg bound value) |> convert typeof<unit>
                | _ ->
                    computedName nameExpr target [ value ] typeof<unit> (fun name targetArg args ->
                        Binders.setMember context name targetArg (List.head args))
                |> Some
            | New(_, argExprs) when argExprs |> List.exists (function OutMarker -> true | _ -> false) ->
                unsupported "Dlr.out in Dlr.new': its result is the constructed T, with no room for an out value (Dlr.ref writes back to a variable)" e
            | New(t, argExprs) when argExprs |> List.exists isByRefMarker ->
                // The site's result is `T` itself (see below): the return value unboxes to it.
                let convertReturn (rt: Type) (e: Expr) = if rt = t then Expr.Call(unboxTo.MakeGenericMethod t, [ e ]) else convert rt e
                Some(byRefCall rewriteIn bound argExprs e.Type convertReturn (fun _ args -> Binders.invokeConstructorByRef context t args))
            | New(t, argExprs) ->
                // The site is typed `T` itself (the binder types a constructor's result as `T`,
                // which an obj-typed site rejects for a struct), so no Convert. One argument of a
                // tuple type is several, as for a member call: F# picks the one-argument overload
                // for `Dlr.new'<T> args` and coerces the tuple.
                let tupleBindings, argExprs =
                    match argExprs with
                    | [ single ] -> splitArgs single
                    | many -> [], many
                let bindings, bound', argExprs =
                    if hoists tupleBindings argExprs then
                        let bindings, vars, _, _, argExprs' = sequenced None [] tupleBindings argExprs
                        bindings, Set.union bound vars, argExprs'
                    else tupleBindings, withTuple bound tupleBindings, argExprs
                match namedOfCall block rewriteIn bound' (Expr.Value(null, typeof<obj>)) argExprs e.Type false (fun _ args -> Binders.invokeConstructor context t args) with
                | Some call -> call |> bind bound bindings |> Some
                | None ->
                    let argBindings, args = argList bound' argExprs
                    Binders.invokeConstructor context t args |> bind bound' argBindings |> bind bound bindings |> Some
            // The value's `?`: applied, a call; read at a function type, the target as that function.
            | Application(EtaReduced(Op opCall [ Unboxed target ]), argExpr)
            | Op opApply [ argExpr; Unboxed target ] when (snd (splitArgs argExpr)) |> List.exists isByRefMarker ->
                let tupleBindings, argExprs = splitArgs argExpr
                if not tupleBindings.IsEmpty then unsupported "Dlr.out / Dlr.ref in a tuple held in a variable" argExpr
                let targetInfo = targetArg bound target
                Some(byRefCall rewriteIn bound argExprs e.Type convert (fun discard args -> Binders.invokeByRef context discard (targetInfo :: args)))
            | Application(EtaReduced(Op opCall [ Unboxed target ]), argExpr)
            | Op opApply [ argExpr; Unboxed target ] ->
                let discard = e.Type = typeof<unit>
                let tupleBindings, argExprs = splitArgs argExpr
                let bindings, bound', target, argExprs =
                    if hoists tupleBindings argExprs then
                        let bindings, vars, target', _, argExprs' = sequenced (Some target) [] tupleBindings argExprs
                        bindings, Set.union bound vars, target'.Value, argExprs'
                    else tupleBindings, withTuple bound tupleBindings, target, argExprs
                match namedOfCall block rewriteIn bound' target argExprs e.Type discard (fun t args -> Binders.invokeOrApply context discard t args) with
                | Some call -> call |> bind bound bindings |> Some
                | None ->
                    let argBindings, args = argList bound' argExprs
                    Binders.invokeOrApply context discard (targetArg bound' target) args |> finish discard e.Type |> bind bound' argBindings |> bind bound bindings |> Some
            | Op opCall [ Unboxed target ] when FSharpType.IsFunction e.Type ->
                Binders.functionTarget context e.Type (targetArg bound target) |> Some
            | Op opCall [ _ ] ->
                unsupported "Dlr.call read at a non-function type (a value read as a type is Dlr.implicit; to invoke, apply it: Dlr.call x (a, b))" e
            | Op opItem [ indexes; Unboxed target ] ->
                let tupleBindings, indexExprs = splitArgs indexes
                let bound' = withTuple bound tupleBindings
                Binders.getIndex context (targetArg bound target) (indexList bound' indexExprs) |> convert e.Type |> bind bound tupleBindings |> Some
            | Op opSetItem [ indexes; value; Unboxed target ] ->
                let tupleBindings, indexExprs = splitArgs indexes
                let bound' = withTuple bound tupleBindings
                Binders.setIndex context (targetArg bound target) (indexList bound' indexExprs) (valueArg bound' value) |> convert typeof<unit> |> bind bound tupleBindings |> Some
            | BinaryOp(op, Unboxed left, Unboxed right) ->
                Binders.binaryOperation context op (valueArg bound left) (valueArg bound right) |> convert e.Type |> Some
            | UnaryOp(op, Unboxed operand) ->
                Binders.unaryOperation context op (valueArg bound operand) |> convert e.Type |> Some
            | Op opCast [ Unboxed value ] ->
                let v = rewrite value
                Binders.convertExplicit context e.Type (if v.Type = typeof<obj> then v else Expr.Coerce(v, typeof<obj>)) |> Some
            | Op opImplicit [ Unboxed value ] ->
                let v = rewrite value
                convert e.Type (if v.Type = typeof<obj> then v else Expr.Coerce(v, typeof<obj>)) |> Some
            // The marker anywhere but as a target would run its getter at run time and throw the
            // outside-a-block error from inside one; say what is wrong instead. Likewise an
            // argument marker anywhere but in a call's argument list (those were consumed above).
            | StaticTarget _ -> unsupported "Dlr.Static<T>.Overloads anywhere but as the target of a call" e
            | Op opNamed _ -> unsupported "Dlr.named anywhere but as an argument of a call (a member call, Dlr.invoke, Dlr.call / Dlr.apply, Dlr.new')" e
            | Op opNamedOf _ -> unsupported "Dlr.namedOf anywhere but as an argument of a call (a member call, Dlr.invoke, Dlr.call / Dlr.apply, Dlr.new')" e
            | Op opArgsOf _ -> unsupported "Dlr.argsOf anywhere but as an argument of a call (a member call, Dlr.invoke, Dlr.call / Dlr.apply, Dlr.new')" e
            | OutMarker | RefMarker _ -> unsupported "Dlr.out / Dlr.ref anywhere but as an argument of a member call (x?M(…), Dlr.get, Dlr.invoke)" e
            | TypeArgs _ -> unsupported "Dlr.typeArgs anywhere but as the first argument of a member call" e
            | _ -> None

    /// Compiles the reflected body of one `dlr { }` block. `closureType` is the block's
    /// compiler-generated container — its state machine struct, or in the fallback path the
    /// class of its `Delay` closure: its fields, named after the captured variables, are where
    /// the body's free variables are read from at call time.
    let translate (builderType: Type) (context: Type) (memberBody: Expr) (closureType: Type) (resultType: Type) (body: Expr) : Compiled =
        let closure = if closureType.IsValueType then Var("sm", closureType) else Var("closure", typeof<obj>)
        let block =
            { BuilderType = builderType
              Context = context
              MemberBody = memberBody
              ClosureType = closureType
              Closure = closure
              Fields = Captures.fields closureType }

        /// Variables bound inside the expression being rewritten are left alone; anything else
        /// that is not the builder comes from the closure or the enclosing member.
        let isCaptured (bound: Set<Var>) (v: Var) = not (bound.Contains v) && v.Type <> block.BuilderType

        let isBuilder (receiver: Expr option) =
            match receiver with
            | Some r -> r.Type = block.BuilderType
            | None -> false

        /// Builder calls and marker operations go to their sections; everything else is generic
        /// rewriting: captured variables become closure reads, and the structure is rebuilt as-is
        /// except where the expression tree has no form for it.
        let rec rewriteIn (bound: Set<Var>) (e: Expr) : Expr =
            let rewrite = rewriteIn bound
            match e with
            | Call(receiver, mi, args) when isBuilder receiver -> Plumbing.call block rewriteIn bound mi args e
            | _ ->
            match Members.tryOperation block rewriteIn bound e with
            | Some rewritten -> rewritten
            | None ->
            match e with
            | Var v when isCaptured bound v -> Captures.read block (rewriteIn bound) v
            | VarSet(v, value) when isCaptured bound v -> Captures.assign block v (rewrite value)
            | ShapeVar _ -> e
            // A resumable-code-typed leftover of the builder's shape (see `codeType`): the
            // compiler's `null` after the rethrow in an unmatched `try … with` arm.
            | Value(null, t) when isCode t -> defaultOf (codeType t)
            // A `let mutable` of the block has no expression-tree form: loop and try bodies are
            // compiled into delegates, and a tree variable cannot be assigned from inside one. So
            // it lives in a reference cell, as the compiler does for a captured mutable, with
            // reads and writes going through the cell.
            | Let(v, def, letBody) when v.IsMutable ->
                let cell = Var(v.Name, typedefof<Ref<_>>.MakeGenericType v.Type)
                let value = cell.Type.GetProperty("Value")
                let rec subst (e: Expr) =
                    match e with
                    | VarSet(v', x) when v' = v -> Expr.PropertySet(Expr.Var cell, value, subst x)
                    | Var v' when v' = v -> Expr.PropertyGet(Expr.Var cell, value)
                    | ShapeVar _ -> e
                    | ShapeLambda(x, b) -> Expr.Lambda(x, subst b)
                    | ShapeCombination(shape, args) -> RebuildShapeCombination(shape, List.map subst args)
                Expr.Let(cell, Expr.NewObject(cell.Type.GetConstructor([| v.Type |]), [ rewrite def ]), rewriteIn (bound.Add cell) (subst letBody))
            | Let(v, def, letBody) -> Expr.Let(v, rewrite def, rewriteIn (bound.Add v) letBody)
            // `let rec` has no expression-tree form; tie the knot through reference cells, as the
            // compiler does: each binding becomes a cell, uses read the cell, and the definitions
            // are assigned after all cells exist so mutual recursion works too.
            | LetRecursive(bindings, letBody) ->
                let cells = [ for (v, _) in bindings -> v, Var(v.Name + "'", typedefof<Ref<_>>.MakeGenericType v.Type) ]
                let readCell (cell: Var) = Expr.PropertyGet(Expr.Var cell, cell.Type.GetProperty("Value"))
                let viaCells (e: Expr) = e.Substitute(fun v -> cells |> List.tryFind (fun (rv, _) -> rv = v) |> Option.map (snd >> readCell))
                let bound = cells |> List.fold (fun (b: Set<Var>) (_, c) -> b.Add c) bound
                let assignments =
                    [ for (_, def), (_, cell) in List.zip bindings cells ->
                        Expr.PropertySet(Expr.Var cell, cell.Type.GetProperty("Value"), rewriteIn bound (viaCells def)) ]
                let body = rewriteIn bound (viaCells letBody)
                let inner = List.foldBack (fun assign rest -> Expr.Sequential(assign, rest)) assignments body
                List.foldBack
                    (fun (v: Var, cell: Var) rest ->
                        Expr.Let(cell, Expr.NewObject(cell.Type.GetConstructor [| v.Type |], [ Expr.Value(null, v.Type) ]), rest))
                    cells inner
            | NewDelegate(t, vars, delegateBody) ->
                // A delegate literal's lambda is the delegate itself, not an F# function: keep it
                // whole (on wasm, made capturing: see `capturing`). The quotation may give the
                // parameters as nested lambdas in the body rather than in `vars`.
                let n = (DelegateMembers.invokeOf t).GetParameters().Length
                let rec peel k (e: Expr) acc =
                    match e with
                    | Lambda(v, b) when k > 0 -> peel (k - 1) b (v :: acc)
                    | _ -> List.rev acc, e
                let peeled, body = peel (n - vars.Length) delegateBody []
                let allVars = vars @ peeled
                let inner = allVars |> List.fold (fun b v -> Set.add v b) bound
                let body = capturing block (asUnit (rewriteIn inner body))
                // .NET Framework's Expression.Lambda finds `Invoke` by public lookup only, and an F#
                // `internal` delegate's is internal: there, the lambda is the Func or Action of the
                // same signature and the delegate is bound over it (#125).
                let invoke = DelegateMembers.invokeOf t
                let parameterTypes = invoke.GetParameters() |> Array.map (fun p -> p.ParameterType)
                let standIn =
                    if onNetFramework && not invoke.IsPublic && parameterTypes.Length <= 16
                       && not (parameterTypes |> Array.exists (fun p -> p.IsByRef)) then
                        Some(if invoke.ReturnType = typeof<Void> then Expression.GetActionType parameterTypes
                             else Expression.GetFuncType(Array.append parameterTypes [| invoke.ReturnType |]))
                    else None
                let literalOf = typedefof<DelegateLiteral<_>>.MakeGenericType t
                match standIn with
                | Some standIn -> Expr.Call(literalOf.GetMethod("From"), [ Expr.Coerce(Expr.NewDelegate(standIn, allVars, body), typeof<Delegate>) ])
                // Re-wrapped so `.Method` is the delegate type's own `Invoke` (see `DelegateLiteral`).
                | None -> Expr.Call(literalOf.GetMethod("Over"), [ Expr.NewDelegate(t, allVars, body) ])
            | ShapeLambda(v, lambdaBody) -> Expr.Lambda(v, capturing block (asUnit (rewriteIn (bound.Add v) lambdaBody)))
            // A void call where a `unit` value is expected (`ignore (list.Add x)`, `f (list.Add x)`):
            // the converter has no value for it, so run it, then `()`.
            | Call(receiver, mi, args) when args |> List.exists isUnitCall ->
                let args' = args |> List.map (fun a -> if isUnitCall a then asUnit (rewrite a) else rewrite a)
                match receiver with
                | Some r -> Expr.Call(rewrite r, mi, args')
                | None -> Expr.Call(mi, args')
            | Application(f, arg) when isUnitCall arg -> Expr.Application(rewrite f, asUnit (rewrite arg))
            | ShapeCombination(shape, args) -> RebuildShapeCombination(shape, List.map rewrite args)

        let delegateType = typedefof<Func<_, _>>.MakeGenericType(closure.Type, resultType)
        let compiled =
            try
                let rewritten = asUnit (rewriteIn Set.empty (normalize body))
                let lambda = Expr.NewDelegate(delegateType, [ closure ], rewritten)
                let linq = LeafExpressionConverter.QuotationToExpression lambda :?> LambdaExpression
                let hoisted = SiteHoister().Visit linq :?> LambdaExpression
                if closureType.IsValueType then
                    // The machine comes by reference (a quotation variable cannot be byref):
                    // copy it into the by-value local the body was converted against. A byref
                    // cannot be closed over, and the copy is free; the reads are off the local.
                    let machine = hoisted.Parameters.[0]
                    let byRef = Expression.Parameter(closureType.MakeByRefType(), "machine")
                    let readerType = typedefof<DlrReader<_, _>>.MakeGenericType(closureType, resultType)
                    Expression.Lambda(readerType, Expression.Block(resultType, [ machine ], Expression.Assign(machine, byRef), hoisted.Body), [ byRef ]).Compile()
                else hoisted.Compile()
            with :? DlrTranslationException -> reraise ()
               // A static member resolved here by reflection and missing is the binder's kind of
               // error, as it would be at the call for an instance target.
               | :? Microsoft.CSharp.RuntimeBinder.RuntimeBinderException -> reraise ()
               | ex -> raise (DlrTranslationException(sprintf "dlr { } could not compile this body: %s\n%A" ex.Message body, ex))
        { Delegate = compiled
          ResultType = resultType }
