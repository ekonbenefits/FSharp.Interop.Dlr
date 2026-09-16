namespace FSharp.Interop.Dlr

open System
open System.Linq.Expressions
open FSharp.Quotations
open FSharp.Quotations.Patterns
open FSharp.Quotations.DerivedPatterns
open FSharp.Quotations.ExprShape
open FSharp.Reflection
open Microsoft.FSharp.Linq.RuntimeHelpers

/// Raised when a `dlr { }` body uses something the translator does not handle.
type DlrTranslationException(message: string) =
    inherit Exception(message)

/// Turns the reflected body of a `dlr { }` block into a `Func<obj, 'T>` over its Delay closure,
/// with the DLR call sites baked in as constants.
module internal Translate =

    /// A compiled `dlr { }` site: a `Func<obj, 'T>` over the Delay closure object.
    type Compiled =
        { Delegate: Delegate
          ResultType: Type }

    let private unsupported (what: string) (e: Expr) =
        raise (DlrTranslationException(sprintf "dlr { } does not support %s: %A" what e))

    let private genericDef (mi: Reflection.MethodInfo) =
        if mi.IsGenericMethod then mi.GetGenericMethodDefinition() else mi

    let private opMethod (e: Expr<_>) =
        match e with
        | Lambdas(_, Call(None, mi, _)) -> genericDef mi
        | _ -> failwith "operator definition expected"

    let private opDynamic = opMethod <@ fun (t: obj) (n: string) -> ((?) t n) : obj @>
    let private opDynamicAssign = opMethod <@ fun (t: obj) (n: string) (v: obj) -> (?<-) t n v @>
    let private opBang = opMethod <@ fun (t: obj) -> ((!?) t) : obj @>
    let private opNamed = opMethod <@ fun (r: obj) -> Dlr.named r @>
    let private opCast = opMethod <@ fun (v: obj) -> Dlr.cast<obj> v @>
    let private unaryOps =
        dict [
            opMethod <@ fun (v: obj) -> (Dlr.neg v) : obj @>, ExpressionType.Negate
            opMethod <@ fun (v: obj) -> (Dlr.not v) : obj @>, ExpressionType.Not
            opMethod <@ fun (v: obj) -> (Dlr.complement v) : obj @>, ExpressionType.OnesComplement
        ]
    let private forEach = opMethod <@ fun (items: seq<obj>) (body: obj -> unit) -> DlrRuntime.forEach items body @>
    let private whileLoop = opMethod <@ fun (guard: unit -> bool) (body: unit -> unit) -> DlrRuntime.whileLoop guard body @>
    let private tryWith = opMethod <@ fun (body: unit -> obj) (handler: exn -> obj) -> DlrRuntime.tryWith body handler @>
    let private tryFinally = opMethod <@ fun (body: unit -> obj) (fin: unit -> unit) -> DlrRuntime.tryFinally body fin @>
    let private using = opMethod <@ fun (r: IDisposable) (body: IDisposable -> obj) -> DlrRuntime.using r body @>
    let private opIdx = opMethod <@ fun (t: obj) -> (Dlr.idx t) : Indexed<obj> @>

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

    /// `Dlr.typeArgs<A, B>()`: the explicit type arguments of the call being built.
    let private (|TypeArgs|_|) (e: Expr) =
        match e with
        | Call(None, mi, []) when mi.DeclaringType = typeof<Dlr> && mi.Name = "typeArgs" -> Some(List.ofArray (mi.GetGenericArguments()))
        | _ -> None

    let private (|Op|_|) (def: Reflection.MethodInfo) (e: Expr) =
        match e with
        | Call(None, mi, args) when genericDef mi = def -> Some args
        | _ -> None

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

    /// `(Dlr.idx x).[i, j]` as a getter or setter: the target and the index expressions.
    let private IndexedProperty (receiver: Expr option, pi: Reflection.PropertyInfo) =
        match receiver with
        | Some(Op opIdx [ Unboxed target ]) when pi.Name = "Item" && pi.DeclaringType.IsGenericType && pi.DeclaringType.GetGenericTypeDefinition() = typedefof<Indexed<_>> ->
            Some target
        | _ -> None

    /// The compiler eta-expands a dynamic member used as a statement:
    /// `let clo = x?Foo in fun a -> clo a` applied to `()`. Fold it back to `x?Foo`.
    let rec private (|EtaReduced|) (e: Expr) =
        match e with
        | Let(v, value, Lambda(x, Application(Var v', Var x'))) when v = v' && x = x' -> value
        | _ -> e

    /// `x?Foo()` applies unit; `x?Foo(a, b)` applies a tuple; `x?Foo(a)` applies one value.
    let private splitArgs (e: Expr) =
        match e with
        | Value(_, t) when t = typeof<unit> -> []
        | NewTuple items -> items
        | single -> [ single ]

    /// Compiles the reflected body of one `dlr { }` block. `closureType` is the compiler-generated
    /// class of the `Delay` closure: its fields, named after the captured variables, are where
    /// the body's free variables are read from at call time.
    /// The definition of a let-bound variable somewhere in `e` (quotation Vars are identity-based,
    /// so shadowing is not a concern).
    let rec private letDefinition (v: Var) (e: Expr) : Expr option =
        match e with
        | Let(v', def, _) when v' = v -> Some def
        | ShapeVar _ -> None
        | ShapeLambda(_, body) -> letDefinition v body
        | ShapeCombination(_, args) -> args |> List.tryPick (letDefinition v)

    let translate (builderType: Type) (context: Type) (memberBody: Expr) (closureType: Type) (resultType: Type) (body: Expr) : Compiled =
        let convert = Binders.convert context
        if closureType.IsGenericType || closureType.ContainsGenericParameters then
            raise (DlrTranslationException(
                    sprintf "dlr { } inside a generic function or member is not supported yet (closure %s)." closureType.Name))

        let closure = Var("closure", typeof<obj>)
        let self = Expr.Coerce(Expr.Var closure, closureType)
        let fields =
            closureType.GetFields(Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic)
            |> Array.map (fun f -> f.Name, f)
            |> dict

        /// A free variable of the body becomes a read of the closure field of the same name.
        /// A captured `let mutable` is stored as an FSharpRef cell; read through it. When there is
        /// no such field the optimizer inlined the variable's definition (a literal, a local
        /// function, ...) instead of capturing it, so substitute that definition from the
        /// enclosing member's body; its own free variables resolve the same way.
        let captured (resolve: Expr -> Expr) (v: Var) : Expr =
            match fields.TryGetValue v.Name with
            | true, f when f.FieldType = v.Type -> Expr.FieldGet(self, f)
            | true, f when f.FieldType.IsGenericType
                           && f.FieldType.GetGenericTypeDefinition() = typedefof<Ref<_>>
                           && f.FieldType.GetGenericArguments().[0] = v.Type ->
                Expr.PropertyGet(Expr.FieldGet(self, f), f.FieldType.GetProperty("Value"))
            | true, f ->
                raise (DlrTranslationException(
                        sprintf "dlr { } captured '%s' as %s but the body uses it as %s." v.Name f.FieldType.Name v.Type.Name))
            | _ ->
                match letDefinition v memberBody with
                | Some def -> resolve def
                | None ->
                    raise (DlrTranslationException(
                            sprintf "dlr { } could not find captured variable '%s' on closure %s (fields: %s) or a let binding for it."
                                v.Name closureType.Name (String.Join(", ", fields.Keys))))

        /// A `unit` expression may compile to a void call, which cannot be the value of a lambda or
        /// of the block; end it with the unit constant so the tree has a `Unit` value.
        let asUnit (e: Expr) =
            if e.Type = typeof<unit> then Expr.Sequential(e, Expr.Value(())) else e

        /// Variables bound inside the expression being rewritten are left alone; anything else
        /// that is not the builder comes from the closure or the enclosing member.
        let isCaptured (bound: Set<Var>) (v: Var) = not (bound.Contains v) && v.Type <> builderType

        let isBuilder (receiver: Expr option) =
            match receiver with
            | Some r -> r.Type = builderType
            | None -> false

        let rec rewriteIn (bound: Set<Var>) (e: Expr) : Expr =
            let rewrite = rewriteIn bound
            match e with
            // CE plumbing (Discover already unwrapped the outermost Delay)
            | Call(receiver, mi, args) when isBuilder receiver ->
                match mi.Name, args with
                | "Return", [ value ] -> rewrite value
                | "Zero", [] -> Expr.Value(())
                | "Combine", [ first; Call(_, d, [ Lambda(_, rest) ]) ] when d.Name = "Delay" ->
                    Expr.Sequential(rewrite first, rewrite rest)
                | "For", [ items; Lambda(x, body) ] ->
                    let items = rewrite items
                    Expr.Call(forEach.MakeGenericMethod(x.Type), [ items; Expr.Lambda(x, asUnit (rewriteIn (bound.Add x) body)) ])
                | "While", [ guard; Call(_, d, [ body ]) ] when d.Name = "Delay" ->
                    Expr.Call(whileLoop, [ rewrite guard; rewrite body ])
                | "TryWith", [ Call(_, d, [ body ]); handler ] when d.Name = "Delay" ->
                    Expr.Call(tryWith.MakeGenericMethod(e.Type), [ rewrite body; rewrite handler ])
                | "TryFinally", [ Call(_, d, [ body ]); compensation ] when d.Name = "Delay" ->
                    Expr.Call(tryFinally.MakeGenericMethod(e.Type), [ rewrite body; rewrite compensation ])
                | "Using", [ resource; Lambda(r, body) ] ->
                    Expr.Call(using.MakeGenericMethod(r.Type, e.Type), [ rewrite resource; Expr.Lambda(r, asUnit (rewriteIn (bound.Add r) body)) ])
                | name, _ -> unsupported (sprintf "the '%s' construct" name) e

            // Dynamic operations
            | Application(EtaReduced(Op opDynamic [ Unboxed target; Literal name ]), argExpr) ->
                let discard = e.Type = typeof<unit>
                let typeArgs, argExpr =
                    match splitArgs argExpr with
                    | TypeArgs ts :: rest -> ts, rest
                    | args -> [], args
                let bindings, args = argList bound argExpr
                Binders.invokeMember context (string name) typeArgs discard (targetArg bound target) args |> finish discard e.Type |> bind bound bindings
            | Op opDynamic [ Unboxed target; Literal name ] ->
                if FSharpType.IsFunction e.Type then
                    unsupported "a dynamic member used as a first-class function; apply it directly" e
                Binders.getMember context (string name) (targetArg bound target) |> convert e.Type
            | Op opDynamicAssign [ Unboxed target; Literal name; Unboxed value ] ->
                Binders.setMember context (string name) (targetArg bound target) (valueArg bound value) |> convert typeof<unit>
            | Application(EtaReduced(Op opBang [ Unboxed target ]), argExpr) ->
                let discard = e.Type = typeof<unit>
                let bindings, args = argList bound (splitArgs argExpr)
                Binders.invoke context discard (targetArg bound target) args |> finish discard e.Type |> bind bound bindings
            | PropertyGet(receiver, pi, indexes) when (IndexedProperty(receiver, pi)).IsSome ->
                let target = (IndexedProperty(receiver, pi)).Value
                Binders.getIndex context (targetArg bound target) (indexList bound indexes) |> convert e.Type
            | PropertySet(receiver, pi, indexes, Unboxed value) when (IndexedProperty(receiver, pi)).IsSome ->
                let target = (IndexedProperty(receiver, pi)).Value
                Binders.setIndex context (targetArg bound target) (indexList bound indexes) (valueArg bound value) |> convert typeof<unit>
            | BinaryOp(op, Unboxed left, Unboxed right) ->
                Binders.binaryOperation context op (valueArg bound left) (valueArg bound right) |> convert e.Type
            | UnaryOp(op, Unboxed operand) ->
                Binders.unaryOperation context op (valueArg bound operand) |> convert e.Type
            | Op opCast [ Unboxed value ] ->
                let v = rewrite value
                Binders.convertExplicit context e.Type (if v.Type = typeof<obj> then v else Expr.Coerce(v, typeof<obj>))

            // Everything else: captured variables become field reads, structure is rebuilt as-is.
            | Var v when isCaptured bound v -> captured (rewriteIn bound) v
            | ShapeVar _ -> e
            | Let(v, def, letBody) -> Expr.Let(v, rewrite def, rewriteIn (bound.Add v) letBody)
            | ShapeLambda(v, lambdaBody) -> Expr.Lambda(v, asUnit (rewriteIn (bound.Add v) lambdaBody))
            | ShapeCombination(shape, args) -> RebuildShapeCombination(shape, List.map rewrite args)

        and targetArg bound (target: Expr) =
            let t = rewriteIn bound target
            Binders.dynamicArg (if t.Type = typeof<obj> then t else Expr.Coerce(t, typeof<obj>))

        and valueArg bound (value: Expr) =
            match value with
            | Value _ -> Binders.constant (Binders.typedArg value)
            | _ -> Binders.typedArg (rewriteIn bound value)

        and argList bound (argExprs: Expr list) =
            let bindings = ResizeArray()
            let args =
                [ for a in argExprs do
                    match a with
                    | TypeArgs _ -> unsupported "Dlr.typeArgs anywhere but as the first argument" a
                    | NamedRecord(lets, fields) ->
                        bindings.AddRange lets
                        let inner = bindings |> Seq.fold (fun (b: Set<Var>) (v, _) -> b.Add v) bound
                        for (name, v) in fields -> Binders.named name (valueArg inner v)
                    | Unboxed v -> yield valueArg bound v ]
            List.ofSeq bindings, args

        and bind bound (bindings: (Var * Expr) list) (call: Expr) =
            List.foldBack (fun (v, value) body -> Expr.Let(v, rewriteIn bound value, body)) bindings call

        and indexList bound (indexes: Expr list) = [ for Unboxed i in indexes -> valueArg bound i ]

        and finish discard (resultType: Type) (call: Expr) =
            if discard then call else convert resultType call

        let rewrite = rewriteIn Set.empty

        let delegateType = typedefof<Func<_, _>>.MakeGenericType(typeof<obj>, resultType)
        let linq =
            try
                let rewritten = asUnit (rewrite body)
                let lambda = Expr.NewDelegate(delegateType, [ closure ], rewritten)
                LeafExpressionConverter.QuotationToExpression lambda :?> LambdaExpression
            with :? DlrTranslationException -> reraise ()
               | ex -> raise (DlrTranslationException(sprintf "dlr { } could not compile this body: %s\n%A" ex.Message body))
        { Delegate = linq.Compile()
          ResultType = resultType }
