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

/// Turns the reflected body of a `dlr { }` block into a delegate over the block's compiler-generated
/// container — a `DlrReader<'SM, 'T>` over its state machine struct, or a `Func<obj, 'T>` over its
/// Delay closure — with the DLR call sites baked in as constants.
module internal Translate =
    open TranslatePatterns
    open SiteHoisting
    open TranslateBlock
    open TranslateMembers

    /// A compiled `dlr { }` site: a `DlrReader<'SM, 'T>` over the state machine struct, or in the
    /// fallback path a `Func<obj, 'T>` over the Delay closure object.
    type Compiled = { Delegate: Delegate }

    /// Receives each block's expression tree, with the block's container type, once its sites are
    /// hoisted: on the state-machine path that is before the by-reference wrapper, which only
    /// copies the machine into the lambda's `sm` and is what `Compile()` then gets. Null, and then
    /// free, unless set: `docs/trees` sets it by reflection to generate the docs' examples.
    type TreeHook private () =
        static member val Sink : Action<Type, LambdaExpression> = null with get, set

    /// Compiles the reflected body of one `dlr { }` block. `closureType` is the block's
    /// compiler-generated container — its state machine struct, or in the fallback path the
    /// class of its `Delay` closure: its fields, named after the captured variables, are where
    /// the body's free variables are read from at call time.
    let translate (name: string) (builderType: Type) (context: Type) (memberBody: Expr) (closureType: Type) (resultType: Type) (body: Expr) : Compiled =
        let closure = if closureType.IsValueType then Var("sm", closureType) else Var("closure", typeof<obj>)
        let fields = Captures.fields closureType
        let reached, aliases = Captures.reached fields memberBody body
        let block =
            { BuilderType = builderType
              Context = context
              MemberBody = memberBody
              ClosureType = closureType
              Closure = closure
              Fields = fields
              Substituted = aliases
              Ambiguous =
                  reached
                  |> List.countBy (fun v -> v.Name) |> List.filter (fun (_, n) -> n > 1) |> List.map fst |> Set.ofList
              Name = name
              Names = Collections.Generic.Dictionary() }

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
            // A mutable struct captured from outside the block, mutated in place (see `inPlace`).
            match inPlace (fun v -> isCaptured bound v && Captures.isCell block v) (Captures.read block rewrite) (Captures.assign block) rewrite e with
            | Some written -> written
            | None ->
            // An element of a captured tuple the optimizer split into a field per element, read
            // directly or through what it inlines: a local function applied to unit, or an
            // immutable alias of an immutable variable (that value) whose name is no other's.
            let rec tupleVar (e: Expr) : Var option =
                match e with
                | Var v when isCaptured bound v && FSharp.Reflection.FSharpType.IsTuple v.Type ->
                    match letDefinition v memberBody with
                    | Some(Var y as d) when block.Substituted.Contains v || (not v.IsMutable && not y.IsMutable && not (block.Ambiguous.Contains y.Name)) ->
                        tupleVar d |> Option.orElse (Some v)
                    | _ -> Some v
                | Application(Var f, Value(_, t)) when t = typeof<unit> && isCaptured bound f && not (block.Fields.ContainsKey f.Name) ->
                    match letDefinition f memberBody with
                    | Some(Lambda(_, b)) -> tupleVar b
                    | _ -> None
                | _ -> None
            let element =
                match e with
                | TupleGet(inner, i) ->
                    tupleVar inner |> Option.bind (fun v -> Captures.element block v i)
                | Call(None, mi, [ inner ]) when mi.DeclaringType.FullName = "Microsoft.FSharp.Core.Operators" && (mi.Name = "Fst" || mi.Name = "Snd") ->
                    tupleVar inner |> Option.bind (fun v -> Captures.element block v (if mi.Name = "Fst" then 0 else 1))
                | _ -> None
            match element with
            | Some read -> read
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
                    match inPlace ((=) v) (fun _ -> Expr.PropertyGet(Expr.Var cell, value)) (fun _ x -> Expr.PropertySet(Expr.Var cell, value, x)) subst e with
                    | Some written -> written
                    | None ->
                    match e with
                    | VarSet(v', x) when v' = v -> Expr.PropertySet(Expr.Var cell, value, subst x)
                    | Var v' when v' = v -> Expr.PropertyGet(Expr.Var cell, value)
                    | _ -> Quotation.rebuild subst e
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
            // A `try` that is not the builder's (under a lambda, inside a delegate literal, or a
            // `try … with` used as a value): LeafExpressionConverter has no form for it, so it runs
            // through the same delegates as the block's own (#158). F#'s filter only repeats the
            // handler's match, which ends in `reraise ()` where no case fits; the handler alone does.
            | TryWith(body, _, _, ex, handler) ->
                let func = Plumbing.func block rewriteIn bound
                Expr.Call(tryWith.MakeGenericMethod(e.Type), [ func [] body; func [ ex ] (Plumbing.rethrowing ex handler) ])
            // `while` and `for i in a .. b` that are not the builder's (under a lambda, in a delegate
            // literal, inside an expression): no LINQ form either, so the same delegates (#162).
            | WhileLoop(guard, body) ->
                let func = Plumbing.func block rewriteIn bound
                Expr.Call(whileLoop, [ func [] guard; func [] body ])
            | ForIntegerRangeLoop(i, low, high, body) ->
                let func = Plumbing.func block rewriteIn bound
                Expr.Call(forRange, [ rewrite low; rewrite high; func [ i ] body ])
            // An element of a struct tuple: the converter's TupleGet looks for a reference tuple's
            // property and rejects a ValueTuple's index (#162), so it is the field itself
            // (`Item1`…`Item7`, then `Rest`), as the byref holder reads it.
            | TupleGet(tuple, index) when tuple.Type.IsValueType ->
                Binders.byRefHolderPath tuple.Type index |> List.fold (fun e f -> Expr.FieldGet(e, f)) (rewrite tuple)
            | TryFinally(body, compensation) ->
                let func = Plumbing.func block rewriteIn bound
                // `use`'s compensation is `if (d :? IDisposable) then d.Dispose() else ()`: a void
                // call and `()` as the branches, which the converter rejects as mismatched.
                let rec unitBranches (c: Expr) =
                    match c with
                    | IfThenElse(cond, a, b) when c.Type = typeof<unit> -> Expr.IfThenElse(cond, unitBranches a, unitBranches b)
                    | _ when isUnitCall c -> Expr.Sequential(c, Expr.Value(()))
                    | _ -> c
                Expr.Call(tryFinally.MakeGenericMethod(e.Type), [ func [] body; func [] (unitBranches compensation) ])
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
                // `internal` delegate's is internal: there, the lambda is at the public delegate type
                // of the same signature and the delegate is bound over it (#125). Past sixteen
                // parameters that stand-in is emitted at run time, which a quotation must not name
                // on wasm (see Binders.WideSite) — never reached there: .NET Framework only.
                let standIn = DelegateMembers.standIn t
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
            | _ -> Quotation.rebuild rewrite e

        let delegateType = typedefof<Func<_, _>>.MakeGenericType(closure.Type, resultType)
        let compiled =
            try
                let rewritten = asUnit (rewriteIn Set.empty (normalize body))
                let lambda = Expr.NewDelegate(delegateType, [ closure ], rewritten)
                let linq = LeafExpressionConverter.QuotationToExpression lambda :?> LambdaExpression
                let hoisted = nameNested block.NameFor (SiteHoister().Visit linq :?> LambdaExpression)
                match TreeHook.Sink with
                | null -> ()
                | sink -> sink.Invoke(closureType, hoisted)
                if closureType.IsValueType then
                    // The machine comes by reference (a quotation variable cannot be byref):
                    // copy it into the by-value local the body was converted against. A byref
                    // cannot be closed over, and the copy is free; the reads are off the local.
                    let machine = hoisted.Parameters.[0]
                    let byRef = Expression.Parameter(closureType.MakeByRefType(), "machine")
                    let readerType = typedefof<DlrReader<_, _>>.MakeGenericType(closureType, resultType)
                    Expression.Lambda(readerType, Expression.Block(resultType, [ machine ], Expression.Assign(machine, byRef), hoisted.Body), name, [ byRef ]).Compile()
                else Expression.Lambda(hoisted.Type, hoisted.Body, name, hoisted.Parameters).Compile()
            with :? DlrTranslationException -> reraise ()
               // A static member resolved here by reflection and missing is the binder's kind of
               // error, as it would be at the call for an instance target.
               | :? Microsoft.CSharp.RuntimeBinder.RuntimeBinderException -> reraise ()
               | ex -> raise (DlrTranslationException(sprintf "dlr { } could not compile this body: %s\n%A" ex.Message body, ex))
        { Delegate = compiled }
