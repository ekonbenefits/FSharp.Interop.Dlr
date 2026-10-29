namespace FSharp.Interop.Dlr.Build

// The capture map's field pass (#259).

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text

/// The capture map's `fields` section: for each dlr { } block, which source variable each field
/// of its Release state machine holds, computed from the compiler's own optimized tree instead of
/// inferred at run time.
///
/// The fields are what code generation makes them (IlxGen: GenStructStateMachine, GetIlxClosureInfo):
/// the free locals of the block's __stateMachine expression after the lowering inlines let-bound
/// resumable code, without values stored as methods (lambda-lifted `f@12`), sorted by the
/// compiler's stamp, named by ChooseUniqueName (`x`, `x0`, …). A source variable is identified by
/// its name and its order among the member's bindings of that name (a quotation's variables carry
/// no position, so this is what the run time can find again); one of the optimizer's temporaries
/// (`x_0`, a split tuple's element) by its tuple's identity and the element, the tuple being the
/// source variable whose value holds the temporary's source range.
///
/// The section's lines (map format 2, see Program.fs): `V field name ordinal` or
/// `E field name ordinal element`, and `N name count`. A split tuple's element the optimizer kept
/// nowhere has no line: the run time recovers it from its definition or refuses (#278). A block
/// any of whose fields is not attributed has no section: it keeps the strict behaviour.
module Fields =

    let flags = BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public

    /// The compiler's stamp of a local value: its creation number, which orders the fields.
    let stamp (v: FSharpMemberOrFunctionOrValue) : int64 =
        let data = typeof<FSharpMemberOrFunctionOrValue>.GetProperty("Data", flags).GetValue v
        let vr = data.GetType().GetProperty("Item", flags).GetValue data
        unbox<int64> (vr.GetType().GetProperty("Stamp", flags).GetValue vr)

    /// IlxGenSupport.ChooseUniqueName.
    let chooseUnique (baseName: string) (taken: Set<string>) =
        if not (taken.Contains baseName) then baseName
        else Seq.initInfinite id |> Seq.map (fun n -> baseName + string n) |> Seq.find (taken.Contains >> not)

    // ---- The fields: free locals of each __stateMachine expression, as code generation takes them.

    let freeLocals (e: FSharpExpr) =
        let read = Dictionary<int64, FSharpMemberOrFunctionOrValue>()
        let bound = HashSet<int64>()
        let rec go (e: FSharpExpr) =
            match e with
            | FSharpExprPatterns.Value v | FSharpExprPatterns.ValueSet(v, _) when not v.IsModuleValueOrMember -> read.[stamp v] <- v
            | FSharpExprPatterns.AddressOf(FSharpExprPatterns.Value v) when not v.IsModuleValueOrMember -> read.[stamp v] <- v
            | _ -> ()
            match e with
            | FSharpExprPatterns.Let((v, _, _), _) -> bound.Add(stamp v) |> ignore
            | FSharpExprPatterns.LetRec(bs, _) -> for (v, _, _) in bs do bound.Add(stamp v) |> ignore
            | FSharpExprPatterns.Lambda(v, _) -> bound.Add(stamp v) |> ignore
            | FSharpExprPatterns.TryWith(_, v1, _, v2, _, _, _) -> bound.Add(stamp v1) |> ignore; bound.Add(stamp v2) |> ignore
            | FSharpExprPatterns.DecisionTree(_, targets) -> for (vs, _) in targets do for v in vs do bound.Add(stamp v) |> ignore
            | _ -> ()
            for x in e.ImmediateSubExpressions do go x
        go e
        [ for KeyValue(s, v) in read do if not (bound.Contains s) then v ]

    let isResumable (v: FSharpMemberOrFunctionOrValue) =
        try v.FullType.HasTypeDefinition && v.FullType.TypeDefinition.DisplayName = "ResumableCode" with _ -> false

    /// The fields, in order: resumable code bound by `let` stands for its definition's free locals
    /// (the lowering inlines it); a lambda-lifted value (`f@12`) is a method, not captured. `taken`:
    /// names the container has already (a delegate closure's: its parameters', GenDelegateExpr).
    let fieldsOfWith (taken: Set<string>) (definitions: IDictionary<int64, FSharpExpr>) (machine: FSharpExpr) =
        let seen = HashSet<int64>()
        let rec expand (vs: FSharpMemberOrFunctionOrValue list) =
            [ for v in vs do
                if seen.Add(stamp v) then
                    match definitions.TryGetValue(stamp v) with
                    | true, d when isResumable v -> yield! expand (freeLocals d)
                    | _ when v.LogicalName.Contains "@" -> ()
                    | _ -> yield v ]
        let vs = expand (freeLocals machine) |> List.sortBy stamp
        let names, _ = ((taken, []), vs) ||> List.fold (fun (taken, acc) v -> let n = chooseUnique v.LogicalName taken in taken.Add n, n :: acc) |> fun (t, acc) -> List.rev acc, t
        List.zip names vs

    let fieldsOf definitions machine = fieldsOfWith Set.empty definitions machine

    /// The block's file and line: the caller information of its outermost Delay (#235). Inlined,
    /// a Delay is `if DlrLocation.Probing then DlrLocation.Answer(file, line) else …`, and the first
    /// Answer in pre-order is the outermost one's (an inner Delay is in an else branch): the block's
    /// own line, as the run time's probe finds it. The machine's MoveNext invokes the block's code
    /// through let-bound resumable code, so a value bound to some is read through its definition, as
    /// the lowering inlines it (`fieldsOf`).
    let blockKey (definitions: IDictionary<int64, FSharpExpr>) (machine: FSharpExpr) =
        let seen = HashSet<int64>()
        let rec find (e: FSharpExpr) =
            match e with
            | FSharpExprPatterns.Call(_, mfv, _, _, args) when mfv.CompiledName = "Answer" && (try mfv.DeclaringEntity.Value.CompiledName = "DlrLocation" with _ -> false) ->
                match args with
                | [ FSharpExprPatterns.Const(:? string as file, _); FSharpExprPatterns.Const(:? int as line, _) ] -> Some(file, line)
                | _ -> None
            | FSharpExprPatterns.Value v when isResumable v && seen.Add(stamp v) ->
                match definitions.TryGetValue(stamp v) with
                | true, d -> find d
                | _ -> None
            | _ -> e.ImmediateSubExpressions |> List.tryPick find
        find machine

    // ---- Identities, from the member's unoptimized tree: a variable's name and order among the
    // member's bindings of that name, as a quotation of the member lays them out (parameters first,
    // then pre-order; a let's value before its variable).

    /// A value found again in another tree: FCS copies the tree it optimizes (new stamps), keeping
    /// each value's name and declaration.
    let sourceKey (v: FSharpMemberOrFunctionOrValue) = v.LogicalName, v.DeclarationLocation.ToString()

    /// A builder's `Run`: DlrBuilder's, or the test suite's closure builder's (`Tests.ClosureBuilder.DlrClosureBuilder`,
    /// the same members). Only a `Delay` carrying caller information makes it a block (`delayKey`).
    let isRun (m: FSharpMemberOrFunctionOrValue) =
        m.CompiledName = "Run" && (try (let n = m.DeclaringEntity.Value.CompiledName in n = "DlrBuilder" || n = "DlrClosureBuilder") with _ -> false)

    /// A block's key in the unoptimized tree: the caller information (file, line) on the Delay that
    /// is Run's argument (#235: `Run(Delay(fun () -> …, file, line))`).
    let delayKey (e: FSharpExpr) : (string * int) option =
        match e with
        | FSharpExprPatterns.Call(_, r, _, _, [ FSharpExprPatterns.Call(_, d, _, _, ds) ]) when isRun r && d.CompiledName = "Delay" ->
            match List.rev ds with
            | FSharpExprPatterns.Const(:? int as line, _) :: FSharpExprPatterns.Const(:? string as file, _) :: _ -> Some(file, line)
            | _ -> None
        | _ -> None

    /// A curried parameter of tuple type compiles as one parameter per element (`x_0`, `x_1`), and
    /// FCS shows them so, compiler-generated and sharing the tuple's range; the quotation has the
    /// one tuple `x`. Such a group: (`x`, its elements).
    let splitParameter (group: FSharpMemberOrFunctionOrValue list) =
        let parts =
            group |> List.mapi (fun i v ->
                let n = v.LogicalName
                let at = n.LastIndexOf '_'
                if at > 0 && n.Substring(at + 1) = string i then Some(n.Substring(0, at)) else None)
        match parts with
        // Compiler-generated, all of them: a user's own `(x_0, x_1)` is named the same but is not
        // a split, and the quotation has those two variables, not a tuple `x`.
        | Some b :: _ when group.Length > 1 && List.forall ((=) (Some b)) parts && group |> List.forall (fun v -> v.IsCompilerGenerated) -> Some b
        | _ -> None

    /// Each binding's construct (the `let`, or the application of a lambda applied on the spot):
    /// a temporary the optimizer makes of the whole binding has its range. Module-level state, as
    /// `splitElements`: the tool runs one pass per process.
    let bindRanges = Dictionary<string * string, range>()

    /// SPIKE: a match target's variable's value at the first leaf that binds it (the unoptimized
    /// tree), for data-flow attribution only.
    let targetValues = Dictionary<string * string, FSharpExpr>()

    /// Split parameters' elements: (name, declaration) → (tuple parameter's name, element).
    let splitElements = Dictionary<string * string, string * int>()

    let bindings (args: FSharpMemberOrFunctionOrValue list list) (body: FSharpExpr) =
        // Each binding in the quotation's order: the value, its definition, and a name the quotation
        // gives it when not its own (a tupled group's `tupledArg`, which has no value in FCS's tree).
        let order = ResizeArray<FSharpMemberOrFunctionOrValue * FSharpExpr option * string option>()
        let add (v: FSharpMemberOrFunctionOrValue, d: FSharpExpr option) = order.Add((v, d, None))
        // A value bound more than once (a guard's variable, bound for the guard and again for its
        // target; a handler's, for the filter and the handler; an or-pattern's target, at each
        // leaf): per source value, its bindings in scope (indices into `order`), innermost on top;
        // and at each block, the one in scope there, which is the one the block reads.
        let active = Dictionary<string * string, Stack<int>>()
        let atBlocks = Dictionary<string * int, Dictionary<string * string, int>>()
        let scoped (bound: (FSharpMemberOrFunctionOrValue * int) list) (scope: unit -> unit) =
            for v, i in bound do
                match active.TryGetValue(sourceKey v) with
                | true, s -> s.Push i
                | _ -> active.[sourceKey v] <- Stack [ i ]
            scope ()
            for v, _ in bound do active.[sourceKey v].Pop() |> ignore
        /// Binds `v` over `scope`.
        let bind (v: FSharpMemberOrFunctionOrValue, d: FSharpExpr option) (scope: unit -> unit) =
            add ((v, d))
            scoped [ v, order.Count - 1 ] scope
        let parameters = ResizeArray<FSharpMemberOrFunctionOrValue * int>()
        for group in args do
            match splitParameter group with
            | Some name ->
                // Bound once, as the quotation's tuple parameter: its first element stands for it.
                add ((group.Head, None))
                parameters.Add((group.Head, order.Count - 1))
                group |> List.iteri (fun i v -> splitElements.[sourceKey v] <- (name, i))
            | None ->
                // A tupled group (`(a, b)`) is one `tupledArg` in the quotation, its elements bound
                // from it: it counts first.
                if group.Length > 1 then order.Add((group.Head, None, Some "tupledArg"))
                for v in group do
                    add ((v, None))
                    parameters.Add((v, order.Count - 1))
        // The body may rebuild a split parameter's tuple (`let x = (x_0, x_1)`): that is the parameter
        // itself, not a binding of its own.
        let rebuilt = HashSet<string * string>()
        // Compiler-named lambda parameters the quotation names after a pattern's variable.
        let renamed = Dictionary<string * string, string>()
        // The decision trees being walked, innermost first: a leaf's targets.
        let trees = Stack<(FSharpMemberOrFunctionOrValue list * FSharpExpr) list>()
        let rec go (e: FSharpExpr) =
            match delayKey e with
            | Some key when not (atBlocks.ContainsKey key) ->
                // An or-pattern's copies of a block share its key: the first stands for it.
                atBlocks.[key] <- Dictionary(active |> Seq.filter (fun kv -> kv.Value.Count > 0) |> Seq.map (fun kv -> KeyValuePair(kv.Key, kv.Value.Peek())))
            | _ -> ()
            match e with
            | FSharpExprPatterns.Let((v, (FSharpExprPatterns.NewTuple(_, es) as d), _), b)
                when not es.IsEmpty
                     && es |> List.forall (function
                        | FSharpExprPatterns.Value w -> (match splitElements.TryGetValue(sourceKey w) with | true, (n, _) -> n = v.LogicalName | _ -> false)
                        | _ -> false) ->
                rebuilt.Add(sourceKey v) |> ignore
                bind (v, Some d) (fun () -> go b)
            | FSharpExprPatterns.Let((v, d, _), b) -> go d; bindRanges.[sourceKey v] <- e.Range; bind (v, Some d) (fun () -> go b)
            // As the quotation has a `let rec` (FSharp.Core's LetRecursive: the variables, then
            // `body :: definitions`): the body before the definitions, or a name bound in both
            // gets its two orders swapped with its count kept (#259's review).
            | FSharpExprPatterns.LetRec(bs, b) ->
                let first = order.Count
                for (v, d, _) in bs do
                    // A closure copy of what the functions capture has the whole construct's range.
                    bindRanges.[sourceKey v] <- e.Range
                    add ((v, Some d))
                scoped (bs |> List.mapi (fun i (v, _, _) -> v, first + i)) (fun () -> go b; for (_, d, _) in bs do go d)
            // A lambda applied on the spot: its parameter's value is the argument. As a quotation
            // has it: the parameter, its body, then the argument.
            | FSharpExprPatterns.Application(FSharpExprPatterns.Lambda(v, b), _, [ arg ]) -> bindRanges.[sourceKey v] <- e.Range; bind (v, Some arg) (fun () -> go b); go arg
            // A compiler-named lambda parameter bound at once to a pattern's variable (`for i in`,
            // `fun (a, b) ->`, `use r`, a handler's `e`: `fun _arg1 -> let i = _arg1 in …`): the
            // quotation names the parameter after that variable, so it counts as one more `i`.
            | FSharpExprPatterns.Lambda(v, (FSharpExprPatterns.Let((w, FSharpExprPatterns.Value u, _), _) as b))
                when v.IsCompilerGenerated && v.LogicalName.StartsWith "_arg" && stamp u = stamp v && not (w.LogicalName.StartsWith "_") ->
                renamed.[sourceKey v] <- w.LogicalName
                bind (v, None) (fun () -> go b)
            | FSharpExprPatterns.Lambda(v, b) -> bind (v, None) (fun () -> go b)
            | FSharpExprPatterns.TryWith(b, v1, f, v2, h, _, _) -> go b; bind (v1, None) (fun () -> go f); bind (v2, None) (fun () -> go h)
            // As the quotation has a match (QuotationTranslator.ConvDecisionTree): a target is
            // inlined at every leaf that reaches it, its variables bound and its body there each
            // time, so a target two leaves reach (an or-pattern binding `x`) binds `x` twice.
            | FSharpExprPatterns.DecisionTree(d, targets) -> trees.Push targets; go d; trees.Pop() |> ignore
            | FSharpExprPatterns.DecisionTreeSuccess(i, es) when trees.Count > 0 && i < trees.Peek().Length ->
                let vs, t = trees.Peek().[i]
                for x in es do go x
                let first = order.Count
                if vs.Length = es.Length then
                    for v, x in List.zip vs es do
                        if not (targetValues.ContainsKey(sourceKey v)) then targetValues.[sourceKey v] <- x
                for v in vs do add ((v, None))
                scoped (vs |> List.mapi (fun i v -> v, first + i)) (fun () -> go t)
            | _ -> for x in e.ImmediateSubExpressions do go x
        scoped (List.ofSeq parameters) (fun () -> go body)
        let counts = Dictionary<string, int>()
        // Each counted binding's ordinal among its name's, by its index in `order`.
        let ordinalAt = Dictionary<int, int>()
        let ordinals = Dictionary<string * string, int>()
        // A target two leaves reach is bound twice: both count, the first stands for the value.
        let seen = HashSet<string * string>()
        // The counted bindings in order, as (name, value): what the layout oracle compares with
        // the quotation's (Companion/oracle.fsx).
        let layout = ResizeArray<string * FSharpMemberOrFunctionOrValue>()
        let entries =
            [ for i in 0 .. order.Count - 1 do
              let v, d, named = order.[i]
              match named with
              | Some name ->
                  // Counted only: no source value of that name to attribute.
                  let n = match counts.TryGetValue name with | true, n -> n | _ -> 0
                  counts.[name] <- n + 1
                  layout.Add((name, v))
              | None ->
                let name =
                    match splitElements.TryGetValue(sourceKey v), renamed.TryGetValue(sourceKey v) with
                    | (true, (b, _)), _ -> b
                    | _, (true, r) -> r
                    | _ -> v.LogicalName
                if rebuilt.Contains(sourceKey v) then
                    // The split parameter of that name, bound last.
                    yield sourceKey v, (v, counts.[name] - 1, d)
                else
                let n = match counts.TryGetValue name with | true, n -> n | _ -> 0
                counts.[name] <- n + 1
                layout.Add((name, v))
                ordinalAt.[i] <- n
                if seen.Add(sourceKey v) then
                    ordinals.[(name, v.DeclarationLocation.ToString())] <- n
                    yield sourceKey v, (v, n, d) ]
        // The other elements of a split parameter share its ordinal.
        let extra =
            [ for group in args do
                match splitParameter group with
                | Some name ->
                    let n = ordinals.[(name, group.Head.DeclarationLocation.ToString())]
                    for v in group.Tail -> sourceKey v, (v, n, None)
                | None -> () ]
        // Per block, a value bound more than once: the ordinal of its binding in scope there (the
        // entries have its first).
        let twice =
            order |> Seq.choose (fun (v, _, named) -> if named.IsNone && not (rebuilt.Contains(sourceKey v)) then Some(sourceKey v) else None)
            |> Seq.countBy id |> Seq.filter (fun (_, n) -> n > 1) |> Seq.map fst |> HashSet
        let inScope =
            dict [ for KeyValue(block, at) in atBlocks ->
                    block, (dict [ for KeyValue(k, i) in at do
                                    match ordinalAt.TryGetValue i with
                                    | true, n when twice.Contains k -> yield k, n
                                    | _ -> () ] :> IDictionary<_, _>) ]
        dict (entries @ extra), (counts :> IReadOnlyDictionary<string, int>), List.ofSeq layout, inScope

    /// The source range a variable's value comes from: its definition, through element projections
    /// of tuple literals (a pattern binds `x` as an element of its input's literal) and union cases'
    /// fields (`| Some x ->`).
    let rec valueRange (bySource: IDictionary<string * string, FSharpMemberOrFunctionOrValue * int * FSharpExpr option>) (e: FSharpExpr) : range option =
        match e with
        | FSharpExprPatterns.TupleGet(_, i, FSharpExprPatterns.Value w) ->
            match bySource.TryGetValue(sourceKey w) with
            | true, (_, _, Some d) ->
                match d with
                | FSharpExprPatterns.NewTuple(_, es) when i < es.Length -> Some es.[i].Range
                | _ -> valueRange bySource d
            | _ -> None
        // A union case's field (`| Some x ->`): the case's argument.
        | FSharpExprPatterns.UnionCaseGet(FSharpExprPatterns.Value w, _, case, field) ->
            match bySource.TryGetValue(sourceKey w) with
            | true, (_, _, Some(FSharpExprPatterns.NewUnionCase(_, case', args))) when case'.Name = case.Name ->
                let i = case.Fields |> Seq.tryFindIndex (fun f -> f.Name = field.Name)
                match i with
                | Some i when i < args.Length -> Some args.[i].Range
                | _ -> None
            | _ -> None
        | _ -> Some e.Range

    let contains (outer: range) (inner: range) =
        outer.FileName = inner.FileName && Range.rangeContainsRange outer inner


    /// Per block key (file, line), the lines of its `fields` section (its machine's) and of its
    /// `closure` section (its closure path's container, #276), for the outermost block of each line
    /// (None: a field not attributed, so the block keeps the strict behaviour on that path).
    let run (unoptimized: FSharpAssemblyContents) (optimized: FSharpAssemblyContents) : IDictionary<string * int, string list option> * IDictionary<string * int, string list option> =
        // ---- The map.

        /// Per block key (file, line), every machine found there: the column of its `dlr` keyword, and its
        /// map lines when every field is attributed. Nested blocks share a line, and only the outermost is
        /// ever looked up (an inner one compiles as part of it), whatever order the optimizer left them in.
        let byKey = Dictionary<string * int, ResizeArray<int * string list option>>()
        /// The same for the closure path's containers (the `closure` section).
        let byClosureKey = Dictionary<string * int, ResizeArray<int * string list option>>()
        /// The unoptimized members, by compiled name and declaration: the optimized tree has more (the
        /// functions it lambda-lifts become declarations of their own), so the two are not paired by
        /// position.
        let unoptimizedMembers =
            let found = Dictionary<string * string, FSharpMemberOrFunctionOrValue list list * FSharpExpr>()
            let rec go (ds: FSharpImplementationFileDeclaration list) =
                for d in ds do
                    match d with
                    | FSharpImplementationFileDeclaration.Entity(_, sub) -> go sub
                    | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(mfv, args, body) ->
                        found.[(mfv.CompiledName, mfv.DeclarationLocation.ToString())] <- (args, body)
                    | FSharpImplementationFileDeclaration.InitAction _ -> ()
            for f in unoptimized.ImplementationFiles do go f.Declarations
            found

        let rec members (opt: FSharpImplementationFileDeclaration list) =
            for o in opt do
                match o with
                | FSharpImplementationFileDeclaration.Entity(_, os) -> members os
                | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(omfv, _, obody) when unoptimizedMembers.ContainsKey((omfv.CompiledName, omfv.DeclarationLocation.ToString())) ->
                    let uargs, ubody = unoptimizedMembers.[(omfv.CompiledName, omfv.DeclarationLocation.ToString())]
                    let definitions = Dictionary<int64, FSharpExpr>()
                    let rec defs (e: FSharpExpr) =
                        match e with
                        | FSharpExprPatterns.Let((v, d, _), _) -> definitions.[stamp v] <- d
                        | _ -> ()
                        for x in e.ImmediateSubExpressions do defs x
                    defs obody
                    // The optimized member's lambda parameters, by stamp (closure conversion's captures among them).
                    let lambdaParameters = HashSet<int64>()
                    let rec lambdas (e: FSharpExpr) =
                        match e with
                        | FSharpExprPatterns.Lambda(b, _) -> lambdaParameters.Add(stamp b) |> ignore
                        | _ -> ()
                        for x in e.ImmediateSubExpressions do lambdas x
                    lambdas obody
                    let bySource, nameCounts, _, inScope = bindings uargs ubody
                    // SPIKE (data-flow attribution): every site that gives a value of the optimized
                    // member its value: a let's definition, the argument a lambda is applied to (also
                    // through a let-bound lambda), each leaf's value for a match target.
                    let sites = Dictionary<int64, ResizeArray<FSharpExpr>>()
                    // Functions by stamp: let- and let rec-bound (a let rec the optimizer gave its
                    // captures as leading parameters is applied to them at each call).
                    let functions = Dictionary<int64, FSharpExpr>(definitions)
                    let rec recs (e: FSharpExpr) =
                        match e with
                        | FSharpExprPatterns.LetRec(bs, _) -> for (v, d, _) in bs do functions.[stamp v] <- d
                        | _ -> ()
                        for x in e.ImmediateSubExpressions do recs x
                    recs obody
                    let site (v: FSharpMemberOrFunctionOrValue) (e: FSharpExpr) =
                        match sites.TryGetValue(stamp v) with
                        | true, l -> l.Add e
                        | _ -> sites.[stamp v] <- ResizeArray [ e ]
                    let rec flow (e: FSharpExpr) =
                        match e with
                        | FSharpExprPatterns.Let((v, d, _), _) -> site v d
                        // The optimizer's own mutables (a branching tuple's `t_0$tupleElem`, set in each
                        // branch); a source mutable is itself, never traced.
                        | FSharpExprPatterns.ValueSet(v, d) -> site v d
                        | FSharpExprPatterns.LetRec(bs, _) -> for (v, d, _) in bs do site v d
                        | FSharpExprPatterns.Application(f, _, args) ->
                            let rec lambdaOf (f: FSharpExpr) =
                                match f with
                                | FSharpExprPatterns.Lambda _ -> Some f
                                | FSharpExprPatterns.Value g when functions.ContainsKey(stamp g) -> lambdaOf functions.[stamp g]
                                | _ -> None
                            let rec bindArgs (l: FSharpExpr) (args: FSharpExpr list) =
                                match l, args with
                                | FSharpExprPatterns.Lambda(p, body), a :: rest -> site p a; bindArgs body rest
                                | _ -> ()
                            match lambdaOf f with
                            | Some l -> bindArgs l args
                            | None -> ()
                        | FSharpExprPatterns.DecisionTree(d, targets) ->
                            let rec leaves (x: FSharpExpr) =
                                match x with
                                | FSharpExprPatterns.DecisionTreeSuccess(i, es) when i < targets.Length ->
                                    let vs, _ = targets.[i]
                                    if vs.Length = es.Length then List.iter2 site vs es
                                | FSharpExprPatterns.DecisionTree _ -> ()
                                | _ -> for y in x.ImmediateSubExpressions do leaves y
                            leaves d
                        | _ -> ()
                        for x in e.ImmediateSubExpressions do flow x
                    flow obody
                    /// A block's section lines for its container's fields (None: one not attributed), added
                    /// to `target` under its key with the rank the outermost is chosen by (the least).
                    let describe (target: Dictionary<string * int, ResizeArray<int * string list option>>) (file: string) (line: int) (column: int) (fields: (string * FSharpMemberOrFunctionOrValue) list) =
                        // A value bound more than once: its binding in scope at this block.
                        let ordinal (sv: FSharpMemberOrFunctionOrValue) (n: int) =
                            match inScope.TryGetValue((file, line)) with
                            | true, at -> (match at.TryGetValue(sourceKey sv) with | true, k -> k | _ -> n)
                            | _ -> n
                        // A value of the optimized member: the source variable (name, ordinal) it is,
                        // and the element when it is one of a split tuple's; None when not attributed.
                        let rec attribute (v: FSharpMemberOrFunctionOrValue) : (string * int * int option) option =
                            match attributeOwn v with
                            | Some a -> Some a
                            | None ->
                                // A copy the optimizer let-binds to another value (an inlined local
                                // function's capture, `let log = log`): that value's attribution, exactly.
                                match definitions.TryGetValue(stamp v) with
                                | true, FSharpExprPatterns.Value u when stamp u <> stamp v -> attribute u
                                | _ -> None
                        and attributeOwn (v: FSharpMemberOrFunctionOrValue) : (string * int * int option) option =
                                match bySource.TryGetValue(sourceKey v) with
                                | true, (_, n, _) when splitElements.ContainsKey(sourceKey v) ->
                                    let name, i = splitElements.[sourceKey v]
                                    Some(name, n, Some i)
                                | true, (sv, n, _) -> Some(sv.LogicalName, ordinal sv n, None)
                                | _ ->
                                    // The optimizer's own: a temporary `<tuple>_<i>` (an element), or a value
                                    // it re-created under the source name (a pattern's). Attributed to the one
                                    // source variable of that name whose value holds its range, whose definition
                                    // has its range, or whose own binding it starts at (the branching
                                    // rewrite's `$tupleElem`); else none.
                                    let r = v.DeclarationLocation
                                    let owner (name: string) (fits: FSharpMemberOrFunctionOrValue -> bool) (byValue: bool) =
                                        let candidates =
                                            bySource.Values |> Seq.filter (fun (sv, _, _) -> sv.LogicalName = name && fits sv) |> List.ofSeq
                                        // The binding the optimizer made the temporary of, whole: exactly one
                                        // whose construct has its range; else by value, below.
                                        match candidates |> List.filter (fun (sv, _, _) -> match bindRanges.TryGetValue(sourceKey sv) with | true, br -> br = r | _ -> false) with
                                        | [ one ] -> [ one ]
                                        | _ when not byValue -> []
                                        | _ ->
                                        candidates
                                        |> Seq.filter (fun (sv, _, d) ->
                                            true
                                            && ((match d |> Option.bind (valueRange bySource) with Some vr -> contains vr r | None -> false)
                                                || (match d with Some d -> d.Range = r | None -> false)
                                                || (match d |> Option.bind (valueRange bySource) with Some vr -> contains r vr | None -> false)
                                                || (sv.DeclarationLocation.FileName = r.FileName && sv.DeclarationLocation.Start = r.Start)))
                                        |> List.ofSeq
                                    let name = v.LogicalName.Replace("$tupleElem", "")
                                    let element =
                                        match name.LastIndexOf '_' with
                                        | at when at > 0 && at < name.Length - 1 && Seq.forall Char.IsDigit (name.Substring(at + 1)) ->
                                            Some(name.Substring(0, at), int (name.Substring(at + 1)))
                                        | _ -> None
                                    match element with
                                    | Some(tuple, i) ->
                                        let elementOf (sv: FSharpMemberOrFunctionOrValue) =
                                            sv.FullType.IsTupleType && i < sv.FullType.GenericArguments.Count
                                            && sv.FullType.GenericArguments.[i].Format(FSharpDisplayContext.Empty) = v.FullType.Format(FSharpDisplayContext.Empty)
                                        match owner tuple elementOf true with
                                        | [ (sv, n, _) ] -> Some(sv.LogicalName, ordinal sv n, Some i)
                                        | _ -> None
                                    | None ->
                                        let sameType (sv: FSharpMemberOrFunctionOrValue) = sv.FullType.Format(FSharpDisplayContext.Empty) = v.FullType.Format(FSharpDisplayContext.Empty)
                                        // A closure's copy of a captured variable: closure conversion (a local
                                        // function or lambda made a closure, or lifted) gives the captured value a
                                        // new Val with the enclosing function's range, its name's or its whole
                                        // binding's (a `let rec`'s: the whole construct). Attributed to the one
                                        // source variable of that name and type declared before that function; with
                                        // two (a shadowed name), none.
                                        let enclosingOf (functions: bool) =
                                            bySource.Values |> Seq.exists (fun (sv, _, _) ->
                                                sv.LogicalName <> v.LogicalName
                                                && (not functions || sv.FullType.IsFunctionType)
                                                && (sv.DeclarationLocation = r || (match bindRanges.TryGetValue(sourceKey sv) with | true, br -> br = r | _ -> false)))
                                        let enclosing = enclosingOf false
                                        // A local function's range holds the definitions of its own variables of
                                        // that name, which its copy is not: never matched by value (#276). Other
                                        // bindings share ranges without being closures (an inlined operator's
                                        // `arg0_0` has its application's), so for them value comes first.
                                        match owner v.LogicalName sameType (not (enclosingOf true)) with
                                        | [ (sv, n, _) ] -> Some(sv.LogicalName, ordinal sv n, None)
                                        | _ ->
                                            let before (sv: FSharpMemberOrFunctionOrValue) =
                                                let d = sv.DeclarationLocation
                                                d.FileName = r.FileName && (d.End.Line < r.Start.Line || (d.End.Line = r.Start.Line && d.End.Column <= r.Start.Column))
                                            // Or a lambda parameter no source binding is: a closure the optimizer made
                                            // (a lambda passed to an inlined local function) takes its captures as
                                            // parameters, with the call's range. A free local of the block can only be
                                            // a variable the block's source names, so it is one in scope at the block:
                                            // the one of that name declared before the block; with two, none.
                                            let lambdaBound = lambdaParameters.Contains(stamp v)
                                            // A mutable captured by a closure arrives as its ref cell.
                                            let fitsType (sv: FSharpMemberOrFunctionOrValue) =
                                                let rec expand (t: FSharpType) = if t.IsAbbreviation then expand t.AbbreviatedType else t
                                                let vt = expand v.FullType
                                                sameType sv
                                                || (sv.IsMutable && vt.HasTypeDefinition && vt.TypeDefinition.TryFullName = Some "Microsoft.FSharp.Core.FSharpRef`1"
                                                    && vt.GenericArguments.[0].Format(FSharpDisplayContext.Empty) = sv.FullType.Format(FSharpDisplayContext.Empty))
                                            let atBlock = Position.mkPos line 0
                                            let beforeBlock (sv: FSharpMemberOrFunctionOrValue) =
                                                Path.GetFileName sv.DeclarationLocation.FileName = Path.GetFileName file && Position.posLt sv.DeclarationLocation.End atBlock
                                            if enclosing then
                                                match bySource.Values |> Seq.filter (fun (sv, _, _) -> sv.LogicalName = v.LogicalName && fitsType sv && before sv) |> List.ofSeq with
                                                | [ (sv, n, _) ] -> Some(sv.LogicalName, ordinal sv n, None)
                                                | _ -> None
                                            elif lambdaBound then
                                                match bySource.Values |> Seq.filter (fun (sv, _, _) -> sv.LogicalName = v.LogicalName && fitsType sv && beforeBlock sv) |> List.ofSeq with
                                                | [ (sv, n, _) ] -> Some(sv.LogicalName, ordinal sv n, None)
                                                | _ -> None
                                            else None
                        let byRules = fields |> List.map (fun (field, v) -> field, attribute v)
                        let mutable attributed = byRules
                        // SPIKE: the same fields attributed by data flow, compared with the rules above;
                        // CMAP_ATTRIBUTION=dataflow writes data flow's answers into the map instead.
                        if Environment.GetEnvironmentVariable "CMAP_DATAFLOW" = "1" || Environment.GetEnvironmentVariable "CMAP_ATTRIBUTION" = "dataflow" then
                            let rec typeOf (t: FSharpType) = if t.IsAbbreviation then typeOf t.AbbreviatedType else t.Format(FSharpDisplayContext.Empty)
                            let valueOf (sv: FSharpMemberOrFunctionOrValue) =
                                match bySource.TryGetValue(sourceKey sv) with
                                | true, (_, _, Some d) -> Some d
                                | _ -> match targetValues.TryGetValue(sourceKey sv) with | true, x -> Some x | _ -> None
                            // The tuple literals a source value is, through an alias, a union case's
                            // field of a case built in the member, an `if`'s branches, a let's body.
                            let rec literals (depth: int) (d: FSharpExpr) : FSharpExpr list list =
                                if depth > 8 then [] else
                                match d with
                                | FSharpExprPatterns.NewTuple(_, es) -> [ es ]
                                | FSharpExprPatterns.Coerce(_, inner) -> literals (depth + 1) inner
                                | FSharpExprPatterns.IfThenElse(_, a, b) -> literals (depth + 1) a @ literals (depth + 1) b
                                | FSharpExprPatterns.Let(_, body) -> literals (depth + 1) body
                                | FSharpExprPatterns.Value w -> valueOf w |> Option.map (literals (depth + 1)) |> Option.defaultValue []
                                // A tuple pattern's variable (`let (x, _) = ((a, b), c)`): element i of its input's literals.
                                | FSharpExprPatterns.TupleGet(_, i, inner) ->
                                    [ for es in literals (depth + 1) inner do
                                        if i < es.Length then yield! literals (depth + 1) es.[i] ]
                                | FSharpExprPatterns.UnionCaseGet(FSharpExprPatterns.Value w, _, case, field) ->
                                    match valueOf w with
                                    | Some(FSharpExprPatterns.NewUnionCase(_, case', args)) when case'.Name = case.Name ->
                                        match case.Fields |> Seq.tryFindIndex (fun f -> f.Name = field.Name) with
                                        | Some i when i < args.Length -> literals (depth + 1) args.[i]
                                        | _ -> []
                                    | _ -> []
                                | _ -> []
                            // What an expression the optimizer substituted for a variable is, by the
                            // source: a variable whose value is exactly it (range and type), or element i
                            // of a source tuple's literal (a split tuple's `let t_0 = a`).
                            let byRange (e: FSharpExpr) =
                                let wholes =
                                    [ for (sv, n, _) in bySource.Values do
                                        if typeOf sv.FullType = typeOf e.Type
                                           && (match valueOf sv |> Option.bind (valueRange bySource) with Some vr -> vr = e.Range | None -> false) then
                                            yield (sv.LogicalName, ordinal sv n, None) ]
                                let elements =
                                    [ for (sv, n, _) in bySource.Values do
                                        match valueOf sv with
                                        | Some d ->
                                            for es in literals 0 d do
                                                for i in 0 .. es.Length - 1 do
                                                    if es.[i].Range = e.Range && typeOf es.[i].Type = typeOf e.Type then yield (sv.LogicalName, ordinal sv n, Some i)
                                        | None -> () ]
                                    |> List.distinct
                                wholes, elements
                            let rec trace (seen: Set<int64>) (v: FSharpMemberOrFunctionOrValue) : Choice<string * int * int option, string> =
                                match bySource.TryGetValue(sourceKey v) with
                                | true, (_, n, _) when splitElements.ContainsKey(sourceKey v) ->
                                    let name, i = splitElements.[sourceKey v]
                                    Choice1Of2(name, n, Some i)
                                | true, (sv, n, _) -> Choice1Of2(sv.LogicalName, ordinal sv n, None)
                                | _ when seen.Contains(stamp v) -> Choice2Of2 "cycle"
                                | _ ->
                                    match sites.TryGetValue(stamp v) with
                                    | true, defs ->
                                        // An optimizer mutable's default initial value, before the branches set it.
                                        let defs =
                                            if v.IsMutable && defs.Count > 1 && (match defs.[0] with FSharpExprPatterns.DefaultValue _ -> true | _ -> false)
                                            then List.ofSeq defs |> List.tail else List.ofSeq defs
                                        let seen = seen.Add(stamp v)
                                        // A recursive call passing the parameter on is no other value: dropped.
                                        let defs = defs |> List.filter (fun d -> traceExpr seen d <> Choice2Of2 "cycle")
                                        match defs with
                                        | [] -> Choice2Of2 "cycle"
                                        | [ one ] -> traceExpr seen one
                                        | _ ->
                                            // Several sites (a branching tuple's element, set in each branch): the
                                            // one reading every site supports, its own value or the literal
                                            // element at its range.
                                            let readings (d: FSharpExpr) =
                                                let own = match traceExpr seen d with Choice1Of2 a -> Set.singleton a | _ -> Set.empty
                                                let wholes, elements = byRange d
                                                own + Set.ofList wholes + Set.ofList elements
                                            match defs |> List.map readings |> Set.intersectMany |> List.ofSeq with
                                            | [ one ] -> Choice1Of2 one
                                            | [] -> Choice2Of2 "no common reading"
                                            | _ -> Choice2Of2 "ambiguous"
                                    | _ -> Choice2Of2 "unbound"
                            and traceExpr (seen: Set<int64>) (e: FSharpExpr) =
                                match e with
                                | FSharpExprPatterns.Value u -> trace seen u
                                | FSharpExprPatterns.Coerce(_, inner) -> traceExpr seen inner
                                | FSharpExprPatterns.TupleGet(_, i, inner) ->
                                    match traceExpr seen inner with
                                    | Choice1Of2(name, n, None) -> Choice1Of2(name, n, Some i)
                                    | Choice1Of2 _ -> Choice2Of2 "nested element"
                                    | r -> r
                                | _ ->
                                    // A whole variable first: a pattern's `a` of `let a, b = …` is also its
                                    // hidden tuple's element, and is itself.
                                    match byRange e with
                                    | [ one ], _ -> Choice1Of2 one
                                    | [], [ one ] -> Choice1Of2 one
                                    | [], [] -> Choice2Of2 "computed"
                                    | _ -> Choice2Of2 "computed, ambiguous"
                            // A value carrying one source variable's name whose value traces to another
                            // (an inlined active pattern's `Data`, whose value is the match input): it
                            // stands for both, and which one the block reads is not data flow's to say.
                            let named (v: FSharpMemberOrFunctionOrValue) =
                                let n = v.LogicalName.Replace("$tupleElem", "")
                                let n = match n.LastIndexOf '_' with
                                        | at when at > 0 && at < n.Length - 1 && Seq.forall Char.IsDigit (n.Substring(at + 1)) -> n.Substring(0, at)
                                        | _ -> n
                                n.TrimEnd([| '0'; '1'; '2'; '3'; '4'; '5'; '6'; '7'; '8'; '9' |]) |> fun t -> if t = "" then n else t
                            let flows =
                                fields |> List.map (fun (_, v) ->
                                    match trace Set.empty v with
                                    | Choice1Of2(name, _, _) when name <> named v && name <> v.LogicalName
                                                                 && bySource.Values |> Seq.exists (fun (sv, _, _) -> sv.LogicalName = named v || sv.LogicalName = v.LogicalName) ->
                                        Choice2Of2 "another variable's name"
                                    | r -> r)
                            if Environment.GetEnvironmentVariable "CMAP_ATTRIBUTION" = "dataflow" then
                                attributed <- List.map2 (fun (field, _) f -> field, (match f with Choice1Of2 a -> Some a | Choice2Of2 _ -> None)) fields flows
                            for ((field, v), (_, old)), flowed in List.zip (List.zip fields byRules) flows do
                                let category =
                                    match old, flowed with
                                    | Some o, Choice1Of2 f when o = f -> "same"
                                    | Some _, Choice1Of2 _ -> "DIFFER"
                                    | Some _, Choice2Of2 why -> "rules-only:" + why
                                    | None, Choice1Of2 _ -> "flow-only"
                                    | None, Choice2Of2 why -> "neither:" + why
                                eprintfn "DFCMP\t%s\t%s:%d\t%s\t%s\t%A\t%A" category (Path.GetFileName file) line field v.LogicalName old flowed
                                if Environment.GetEnvironmentVariable "CMAP_UNBOUND" = "1" && flowed = Choice2Of2 "unbound" then
                                    // Where is it bound? Search every optimized declaration.
                                    let rec find (where: string) (e: FSharpExpr) =
                                        match e with
                                        | FSharpExprPatterns.Lambda(b, _) when stamp b = stamp v -> eprintfn "UNBOUND %s found as a lambda parameter in %s at %O" v.LogicalName where e.Range
                                        | FSharpExprPatterns.Let((b, _, _), _) when stamp b = stamp v -> eprintfn "UNBOUND %s found as a let in %s" v.LogicalName where
                                        | FSharpExprPatterns.LetRec(bs, _) when bs |> List.exists (fun (b, _, _) -> stamp b = stamp v) -> eprintfn "UNBOUND %s found in a let rec in %s" v.LogicalName where
                                        | FSharpExprPatterns.ObjectExpr _ -> ()
                                        | _ -> ()
                                        for x in e.ImmediateSubExpressions do find where x
                                    let rec decls (ds: FSharpImplementationFileDeclaration list) =
                                        for d in ds do
                                            match d with
                                            | FSharpImplementationFileDeclaration.Entity(_, sub) -> decls sub
                                            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(m, args, body) ->
                                                if args |> List.concat |> List.exists (fun a -> stamp a = stamp v) then eprintfn "UNBOUND %s is a parameter of declaration %s" v.LogicalName m.CompiledName
                                                find m.CompiledName body
                                            | _ -> ()
                                    for f in optimized.ImplementationFiles do decls f.Declarations
                                    eprintfn "UNBOUND %s stamp %d: free in %s's body: %b" v.LogicalName (stamp v) omfv.CompiledName (freeLocals obody |> List.exists (fun f -> stamp f = stamp v))
                        // Two fields of one variable of a name the member binds more than once: one of
                        // them may be the other binding's. (Of a name bound once, both are that one: the
                        // optimizer keeps a copy beside it.)
                        let duplicated =
                            attributed |> List.choose snd |> List.countBy id
                            |> List.exists (fun ((name, _, _), n) -> n > 1 && (match nameCounts.TryGetValue name with | true, c -> c > 1 | _ -> true))
                        if duplicated then
                            eprintfn "DUPLICATED two fields attributed to one variable, block %s:%d: %s (bindings: %s)" (Path.GetFileName file) line (attributed |> List.map (fun (f, a) -> sprintf "%s=%A" f a) |> String.concat ", ") (attributed |> List.choose snd |> List.map (fun (n, _, _) -> sprintf "%s %d" n (match nameCounts.TryGetValue n with | true, c -> c | _ -> 0)) |> List.distinct |> String.concat ", ")
                        let entries =
                            attributed |> List.map (fun (field, a) ->
                                match a with
                                | Some(name, n, None) -> Some(sprintf "V\t%s\t%s\t%d" field name n)
                                | Some(name, n, Some i) -> Some(sprintf "E\t%s\t%s\t%d\t%d" field name n i)
                                | None -> None)
                        if not (List.forall Option.isSome entries) then
                            for (field, v), entry in List.zip fields entries do
                                if entry.IsNone then
                                    eprintfn "UNATTRIBUTED %s (%s: %s at %s) block %s:%d" field v.LogicalName (v.FullType.Format(FSharpDisplayContext.Empty)) (v.DeclarationLocation.ToString()) (Path.GetFileName file) line
                                    // CMAP_DEBUG=1: how the optimized member binds the field's value.
                                    if Environment.GetEnvironmentVariable "CMAP_DEBUG" = "1" then
                                        let rec binder (e: FSharpExpr) =
                                            match e with
                                            | FSharpExprPatterns.Let((b, d, _), _) when stamp b = stamp v -> eprintfn "    Let, defined as %A" (try d.ToString().Substring(0, min 300 (d.ToString().Length)) with _ -> "?")
                                            | FSharpExprPatterns.Lambda(b, _) when stamp b = stamp v -> eprintfn "    Lambda parameter at %O" e.Range
                                            | FSharpExprPatterns.LetRec(bs, _) when bs |> List.exists (fun (b, _, _) -> stamp b = stamp v) -> eprintfn "    LetRec"
                                            | _ -> ()
                                            for x in e.ImmediateSubExpressions do binder x
                                        binder obody
                                        eprintfn "    (searched the optimized member %s)" omfv.CompiledName
                        // Per name the entries use, how many bindings of it the member has as the
                        // quotation lays them out: the run time counts the quotation's and refuses
                        // the block on any difference, so a layout this pass got wrong is a refusal.
                        let counted =
                            [ for a in attributed do
                                match snd a with
                                | Some(name, _, _) -> yield name
                                | None -> () ]
                            |> List.distinct
                            |> List.map (fun name -> sprintf "N\t%s\t%d" name (match nameCounts.TryGetValue name with | true, n -> n | _ -> 0))
                        let block =
                            if List.forall Option.isSome entries && not duplicated then
                                Some([ sprintf "B\t%s\t%d" file line; "S\tfields" ] @ (entries |> List.map Option.get) @ counted)
                            else None
                        match target.TryGetValue((file, line)) with
                        | true, found -> found.Add((column, block))
                        | _ -> target.[(file, line)] <- ResizeArray [ (column, block) ]
                    let rec machines (e: FSharpExpr) =
                        match e with
                        | FSharpExprPatterns.Call(_, mfv, _, _, _) when mfv.CompiledName = "__stateMachine" ->
                            // Only a dlr { } block's machine carries its file and line (a task { }'s does not).
                            match blockKey definitions e with
                            | None -> ()
                            | Some(file, line) ->
                                describe byKey file line e.Range.StartColumn (fieldsOf definitions e)
                        // The closure path (#276): a block the compiler made no machine of runs
                        // `DlrRun.Closure(builder, code)`, `code` a ResumableCode delegate over the
                        // block's inlined Delay, and its closure class is the container: the delegate's
                        // lambda's free locals (GenDelegateExpr, its parameter `sm`'s name taken). Only
                        // a delegate built here: one not (Delay called, not inlined) is another container.
                        | FSharpExprPatterns.Call(_, mfv, _, _, [ _; code ]) when mfv.CompiledName = "Closure" && (try mfv.DeclaringEntity.Value.CompiledName = "DlrRun" with _ -> false) ->
                            let built =
                                match code with
                                | FSharpExprPatterns.Value c when definitions.ContainsKey(stamp c) -> definitions.[stamp c]
                                | c -> c
                            match built with
                            | FSharpExprPatterns.NewDelegate(_, (FSharpExprPatterns.Lambda(sm, _) as lambda)) ->
                                match blockKey definitions lambda with
                                | Some(file, line) ->
                                    // Nested blocks share a line, and this call's range is the block's
                                    // closing brace (the inner one's comes first), so the outermost is the
                                    // one with the others' closure calls inside it: the most, first.
                                    let rec nested (x: FSharpExpr) =
                                        match x with
                                        | FSharpExprPatterns.Call(_, m, _, _, _) when m.CompiledName = "Closure" && (try m.DeclaringEntity.Value.CompiledName = "DlrRun" with _ -> false) ->
                                            1 + (x.ImmediateSubExpressions |> List.sumBy nested)
                                        | _ -> x.ImmediateSubExpressions |> List.sumBy nested
                                    describe byClosureKey file line (-(nested lambda)) (fieldsOfWith (set [ sm.LogicalName ]) definitions lambda)
                                | None -> ()
                            | _ -> ()
                            for x in e.ImmediateSubExpressions do machines x
                        | _ -> for x in e.ImmediateSubExpressions do machines x
                    machines obody
                | _ -> ()
        for o in optimized.ImplementationFiles do
            members o.Declarations
        // The outermost block of each line: its `dlr` comes first (an inner one is inside its braces;
        // two side by side on one line the run time refuses before reading the map). Its lines without
        // the `B` and `S fields` the writer adds.
        let outermost (found: Dictionary<string * int, ResizeArray<int * string list option>>) =
            dict [ for KeyValue(key, found) in found -> key, (found |> Seq.minBy fst |> snd |> Option.map (List.skip 2)) ]
        outermost byKey, outermost byClosureKey
