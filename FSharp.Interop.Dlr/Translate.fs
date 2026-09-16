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

    let private (|Op|_|) (def: Reflection.MethodInfo) (e: Expr) =
        match e with
        | Call(None, mi, args) when genericDef mi = def -> Some args
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
    let translate (builderType: Type) (closureType: Type) (resultType: Type) (body: Expr) : Compiled =
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
        /// A captured `let mutable` is stored as an FSharpRef cell; read through it.
        let captured (v: Var) : Expr =
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
                raise (DlrTranslationException(
                        sprintf "dlr { } could not find captured variable '%s' on closure %s (fields: %s)."
                            v.Name closureType.Name (String.Join(", ", fields.Keys))))

        /// A `unit` expression may compile to a void call, which cannot be the value of a lambda or
        /// of the block; end it with the unit constant so the tree has a `Unit` value.
        let asUnit (e: Expr) =
            if e.Type = typeof<unit> then Expr.Sequential(e, Expr.Value(())) else e

        let free = body.GetFreeVars() |> Seq.filter (fun v -> v.Type <> builderType) |> Set.ofSeq

        let isBuilder (receiver: Expr option) =
            match receiver with
            | Some r -> r.Type = builderType
            | None -> false

        let rec rewrite (e: Expr) : Expr =
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
                    Expr.Call(forEach.MakeGenericMethod(x.Type), [ items; Expr.Lambda(x, asUnit (rewrite body)) ])
                | "While", [ guard; Call(_, d, [ body ]) ] when d.Name = "Delay" ->
                    Expr.Call(whileLoop, [ rewrite guard; rewrite body ])
                | "TryWith", [ Call(_, d, [ body ]); handler ] when d.Name = "Delay" ->
                    Expr.Call(tryWith.MakeGenericMethod(e.Type), [ rewrite body; rewrite handler ])
                | "TryFinally", [ Call(_, d, [ body ]); compensation ] when d.Name = "Delay" ->
                    Expr.Call(tryFinally.MakeGenericMethod(e.Type), [ rewrite body; rewrite compensation ])
                | "Using", [ resource; Lambda(r, body) ] ->
                    Expr.Call(using.MakeGenericMethod(r.Type, e.Type), [ rewrite resource; Expr.Lambda(r, asUnit (rewrite body)) ])
                | name, _ -> unsupported (sprintf "the '%s' construct" name) e

            // Dynamic operations
            | Application(EtaReduced(Op opDynamic [ Unboxed target; Literal name ]), argExpr) ->
                let discard = e.Type = typeof<unit>
                let bindings, args = argList argExpr
                Binders.invokeMember (string name) discard (targetArg target) args |> finish discard e.Type |> bind bindings
            | Op opDynamic [ Unboxed target; Literal name ] ->
                if FSharpType.IsFunction e.Type then
                    unsupported "a dynamic member used as a first-class function; apply it directly" e
                Binders.getMember (string name) (targetArg target) |> Binders.convert e.Type
            | Op opDynamicAssign [ Unboxed target; Literal name; Unboxed value ] ->
                Binders.setMember (string name) (targetArg target) (valueArg value) |> Binders.convert typeof<unit>
            | Application(EtaReduced(Op opBang [ Unboxed target ]), argExpr) ->
                let discard = e.Type = typeof<unit>
                let bindings, args = argList argExpr
                Binders.invoke discard (targetArg target) args |> finish discard e.Type |> bind bindings
            | PropertyGet(receiver, pi, indexes) when (IndexedProperty(receiver, pi)).IsSome ->
                let target = (IndexedProperty(receiver, pi)).Value
                Binders.getIndex (targetArg target) (indexList indexes) |> Binders.convert e.Type
            | PropertySet(receiver, pi, indexes, Unboxed value) when (IndexedProperty(receiver, pi)).IsSome ->
                let target = (IndexedProperty(receiver, pi)).Value
                Binders.setIndex (targetArg target) (indexList indexes) (valueArg value) |> Binders.convert typeof<unit>
            | BinaryOp(op, Unboxed left, Unboxed right) ->
                Binders.binaryOperation op (valueArg left) (valueArg right) |> Binders.convert e.Type

            // Everything else: captured variables become field reads, structure is rebuilt as-is.
            | Var v when free.Contains v -> captured v
            | ShapeVar _ -> e
            | ShapeLambda(v, body) -> Expr.Lambda(v, asUnit (rewrite body))
            | ShapeCombination(shape, args) -> RebuildShapeCombination(shape, List.map rewrite args)

        and targetArg (target: Expr) =
            let t = rewrite target
            Binders.dynamicArg (if t.Type = typeof<obj> then t else Expr.Coerce(t, typeof<obj>))

        and valueArg (value: Expr) = Binders.typedArg (rewrite value)

        and argList (argExpr: Expr) =
            let bindings = ResizeArray()
            let args =
                [ for a in splitArgs argExpr do
                    match a with
                    | NamedRecord(lets, fields) ->
                        bindings.AddRange lets
                        for (name, v) in fields -> Binders.named name (valueArg v)
                    | Unboxed v -> yield valueArg v ]
            List.ofSeq bindings, args

        and bind (bindings: (Var * Expr) list) (call: Expr) =
            List.foldBack (fun (v, value) body -> Expr.Let(v, rewrite value, body)) bindings call

        and indexList (indexes: Expr list) = [ for Unboxed i in indexes -> valueArg i ]

        and finish discard (resultType: Type) (call: Expr) =
            if discard then call else Binders.convert resultType call

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
