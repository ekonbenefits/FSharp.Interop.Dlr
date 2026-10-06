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

/// One block's translation context (`Block`) and what every rewrite of it shares: where its
/// free variables come from (`Captures`), the builder's own members (`Plumbing`), and the
/// helpers they use.
module internal TranslateBlock =
    open TranslatePatterns
    open SiteHoisting

    /// What every part of the translation of one block needs: where it is (the builder and the
    /// member it sits in) and where its values come from (the state machine struct or the Delay
    /// closure — the same contract: the captured variables are its fields, by name).
    type Block =
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
          Fields: Collections.Generic.IDictionary<string, Reflection.FieldInfo>
          /// The compiled block's name, `dlr@Program.fs:7`: its stack frame's name.
          Name: string
          /// How many nested lambdas of each kind (`for`, `try`, `fun`, …) have been named so far.
          Names: Collections.Generic.Dictionary<string, int>
          /// Names two of the variables the block reaches share (#196): its free variables, and
          /// those of the definitions it recovers for what the optimizer inlined (`Captures.reached`).
          Ambiguous: Set<string>
          /// Aliases (`let x = y` of an immutable `y`, `let x = 1`) the block reaches only through
          /// a recovered definition: substituted, never a field, whatever field shares the name.
          Substituted: Set<Var> }
        /// The name for this block's next compiled part of `kind`, for its stack frame: F#'s
        /// closure style, `dlr@Program.fs:7-for`, then `-for-2`, `-for-3` in order.
        member this.NameFor (kind: string) =
            let n = (match this.Names.TryGetValue kind with | true, n -> n | _ -> 0) + 1
            this.Names.[kind] <- n
            if n = 1 then sprintf "%s-%s" this.Name kind else sprintf "%s-%s-%d" this.Name kind n
        member this.Self =
            if this.ClosureType.IsValueType then Expr.Var this.Closure
            else Expr.Coerce(Expr.Var this.Closure, this.ClosureType)
        member this.Convert (resultType: Type) (e: Expr) = Binders.convert this.Context resultType e

    /// The builder's members type as `ResumableCode<'D, 'R>` — the shape the compiler's state
    /// machine needs — where the translation produces the plain `'R` value. `codeType` is what
    /// such an expression's type becomes; an expression typed that way that is not a builder call
    /// (the compiler's default value in an unmatched `try … with` arm) is retyped to it so the
    /// rebuilt tree is consistent.
    let isCode (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Microsoft.FSharp.Core.CompilerServices.ResumableCode<_, _>>
    let codeType (t: Type) = if isCode t then t.GetGenericArguments().[1] else t
    let defaultOf (t: Type) =
        if t = typeof<unit> then Expr.Value(())
        elif t.IsValueType then Expr.DefaultValue t
        else Expr.Value(null, t)

    /// The generic rewriter, threaded through the sections so each can rewrite a subexpression:
    /// the variables bound inside the expression so far, and the expression.
    type Rewrite = Set<Var> -> Expr -> Expr

    /// A `unit` expression may compile to a void call, which cannot be the value of a lambda or
    /// of the block; end it with the unit constant so the tree has a `Unit` value.
    let asUnit (e: Expr) =
        if e.Type = typeof<unit> then Expr.Sequential(e, Expr.Value(())) else e

    /// A `unit`-typed call or application — possibly a void method, which the converter cannot
    /// use as a value — as opposed to `()` or a unit-typed variable.
    let isUnitCall (e: Expr) =
        e.Type = typeof<unit> && (match e with Call _ | Application _ -> true | _ -> false)

    /// On Mono's browser-wasm runtime a nested lambda that captures nothing loses its arguments
    /// when invoked (see DlrRuntime); one that captures is fine. So there, a nested delegate
    /// body is made to capture the block's closure parameter, which costs nothing elsewhere.
    let capturing (block: Block) (body: Expr) =
        if onWasm then Expr.Let(Var("captured", block.Closure.Type), Expr.Var block.Closure, body) else body

    /// The struct variable a receiver is rooted at, with the struct fields down from it:
    /// `v`, `v.Inner`, `v.Inner.Point`.
    let rec structPath (e: Expr) : (Var * Reflection.FieldInfo list) option =
        match e with
        | Var v when v.Type.IsValueType -> Some(v, [])
        | FieldGet(Some inner, f) when f.FieldType.IsValueType -> structPath inner |> Option.map (fun (v, fs) -> v, fs @ [ f ])
        | _ -> None

    /// A struct variable that lives in a cell — a `let mutable` of the block, or a mutable captured
    /// from outside it, which the compiler stores as an FSharpRef — reads back as a copy, so an
    /// operation that mutates it in place (a field set, a property setter or a method, on the
    /// variable or a struct field of it) would be lost (#162). It runs on a temporary copy that is
    /// written back, in a finally. The arguments are evaluated first, left to right, into temporaries: the
    /// receiver is a variable, so this is F#'s order, and an argument that mutates the variable
    /// itself is not overwritten by a copy read before it ran. The copy is immutable to F# — so it
    /// is not celled again — but a LINQ variable, which a field set or call mutates in place.
    /// `isTarget` picks the cell variables; `read` and `write` are the cell's; `recurse` rewrites
    /// the arguments. None when `e` is not such an operation.
    let inPlace (isTarget: Var -> bool) (read: Var -> Expr) (write: Var -> Expr -> Expr) (recurse: Expr -> Expr) (e: Expr) : Expr option =
        let rooted (receiver: Expr) =
            match structPath receiver with
            | Some(v, fields) when isTarget v -> Some(v, fields)
            | _ -> None
        let apply (v: Var) (fields: Reflection.FieldInfo list) (args: Expr list) (op: Expr -> Expr list -> Expr) =
            let temps = args |> List.mapi (fun i a -> Var(sprintf "arg%d" i, a.Type), recurse a)
            let copy = Var(v.Name + "Copy", v.Type)
            let receiver = fields |> List.fold (fun r f -> Expr.FieldGet(r, f)) (Expr.Var copy)
            let result = op receiver [ for t, _ in temps -> Expr.Var t ]
            // Unit-typed both, as values: a void call or setter cannot be an argument.
            let asValue (e: Expr) = if e.Type = typeof<unit> then Expr.Sequential(e, Expr.Value(())) else e
            // The write-back in a finally (`Binders.InPlace`, made a TryFinally after conversion),
            // so a member that mutates then throws keeps its mutation, as in plain F#; the
            // arguments ran before, outside it, so a throwing argument writes nothing back.
            let body =
                Expr.Call(typeof<Binders.InPlace>.GetMethod("Then", Reflection.BindingFlags.Static ||| Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic).MakeGenericMethod(result.Type),
                          [ asValue result; asValue (write v (Expr.Var copy)) ])
            List.foldBack (fun (t, a) inner -> Expr.Let(t, a, inner)) temps (Expr.Let(copy, read v, body))
        match e with
        | FieldSet(Some receiver, f, x) ->
            rooted receiver |> Option.map (fun (v, fields) -> apply v fields [ x ] (fun r a -> Expr.FieldSet(r, f, List.head a)))
        | PropertySet(Some receiver, p, indexes, x) ->
            rooted receiver |> Option.map (fun (v, fields) ->
                apply v fields (indexes @ [ x ]) (fun r a -> Expr.PropertySet(r, p, List.last a, List.take indexes.Length a)))
        | Call(Some receiver, mi, args) ->
            rooted receiver |> Option.map (fun (v, fields) -> apply v fields args (fun r a -> Expr.Call(r, mi, a)))
        | _ -> None

    /// Where a free variable of the body comes from: the Delay closure, or failing that the
    /// enclosing member's body.
    module Captures =

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

        /// The variables a block reaches: its free variables and, for each one with no field of
        /// its name, those of the definition `read` would recover for it. An alias reached that
        /// way (an immutable `let` of an immutable variable or of a literal) is the optimizer's to
        /// substitute, never a field, so it stands for what it names: it is returned apart, not
        /// counted with the rest. One of a mutable it keeps, holding the value at the time.
        let reached (fields: Collections.Generic.IDictionary<string, Reflection.FieldInfo>) (memberBody: Expr) (body: Expr) : Var list * Set<Var> =
            let own = body.GetFreeVars() |> Set.ofSeq
            let rec go (seen: Set<Var>) counted aliases (pending: Var list) =
                match pending with
                | [] -> counted, aliases
                | v :: rest when seen.Contains v -> go seen counted aliases rest
                | v :: rest ->
                    let seen = seen.Add v
                    let def = letDefinition v memberBody
                    let alias = not v.IsMutable && not (own.Contains v) && (match def with Some(Var y) -> not y.IsMutable | Some(Value _) -> true | _ -> false)
                    let inner =
                        if fields.ContainsKey v.Name && not alias then []
                        else
                            match def |> Option.orElse (parameterArgument v memberBody) with
                            | Some d -> List.ofSeq (d.GetFreeVars())
                            | None -> []
                    if alias then go seen counted (Set.add v aliases) (inner @ rest)
                    else go seen (v :: counted) aliases (inner @ rest)
            go Set.empty [] Set.empty (List.ofSeq own)

        /// Two variables of one name, one reached through a local function or alias the optimizer
        /// inlined: the compiler names their fields `x`, `x0`, … in an order of its own, and a
        /// recovered definition would run again, so neither tells which is which (#196).
        let private ambiguous (v: Var) =
            DlrTranslationException(
                sprintf "dlr { } reaches two variables named '%s', one through a local function or alias the optimizer inlined; rename one." v.Name)

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
            if block.Ambiguous.Contains v.Name then raise (ambiguous v)
            match (if block.Substituted.Contains v then (false, null) else block.Fields.TryGetValue v.Name) with
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

        /// Whether a captured variable is a mutable, stored in an FSharpRef cell.
        let isCell (block: Block) (v: Var) =
            match block.Fields.TryGetValue v.Name with
            | true, f when not (block.Ambiguous.Contains v.Name) -> isRefCell f v.Type
            | _ -> false

        /// `v <- value` on a captured `let mutable`: a write through its FSharpRef cell.
        let assign (block: Block) (v: Var) (value: Expr) : Expr =
            if block.Ambiguous.Contains v.Name then raise (ambiguous v)
            match block.Fields.TryGetValue v.Name with
            | true, f when isRefCell f v.Type ->
                Expr.PropertySet(Expr.FieldGet(block.Self, f), f.FieldType.GetProperty("Value"), value)
            | _ ->
                raise (DlrTranslationException(
                        sprintf "dlr { } assigns '%s', which is not a captured mutable on closure %s (fields: %s)."
                            v.Name block.ClosureType.Name (String.Join(", ", block.Fields.Keys))))

    /// The builder's own methods: `Return`/`Zero`/`Combine` fold away, and the control-flow
    /// members become `DlrRuntime` calls over delegates.
    module Plumbing =

        /// A body as a `Func<..>` delegate over `vars` (see DlrRuntime for why not an F# function).
        /// The delegate's type is built from the variables' types and the body's; a `unit` body
        /// (which may compile to a void call) becomes a `Func<.., unit>`, not an `Action`.
        /// A raw `try … with`'s handler, run as a delegate (`DlrRuntime.tryWith`), is outside any
        /// catch block, so its `reraise ()` — F# puts one where no case matches — throws the
        /// caught exception `ex` again instead (`DlrRuntime.rethrow`, keeping its stack trace). A
        /// nested `try … with` keeps its own handler's. (The builder's handlers need none: there
        /// the compiler's unmatched case is already an ExceptionDispatchInfo rethrow, and an
        /// explicit `reraise ()` does not compile.)
        let rethrowing (ex: Var) (handler: Expr) : Expr =
            let rec go (e: Expr) =
                match e with
                | Call(None, mi, []) when mi.IsGenericMethod && mi.GetGenericMethodDefinition() = reraiseMethod ->
                    Expr.Call(rethrow.GetGenericMethodDefinition().MakeGenericMethod(e.Type), [ Expr.Var ex ])
                | TryWith(body, fv, filter, cv, catch) -> Expr.TryWith(go body, fv, filter, cv, catch)
                | _ -> Quotation.rebuild go e
            go handler

        let func (block: Block) (rewriteIn: Rewrite) (bound: Set<Var>) (vars: Var list) (body: Expr) : Expr =
            let bound = vars |> List.fold (fun b v -> Set.add v b) bound
            let body = capturing block (asUnit (rewriteIn bound body))
            // A parameterless delegate whose body is itself a lambda (`try (fun x -> …) with …`)
            // reads back from FSharp.Core as a delegate of that lambda's parameter: hold the lambda
            // in a `let` so it stays the result.
            let body =
                match vars, body with
                | [], Lambda _ -> let result = Var("result", body.Type) in Expr.Let(result, body, Expr.Var result)
                | _ -> body
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
