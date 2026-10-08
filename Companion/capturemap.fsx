// The capture map (prototype of the build companion): for each dlr { } block, which source
// variable each field of its Release state machine holds, computed from the compiler's own
// optimized tree instead of inferred at run time.
//
// The fields are what code generation makes them (IlxGen: GenStructStateMachine, GetIlxClosureInfo):
// the free locals of the block's __stateMachine expression after the lowering inlines let-bound
// resumable code, without values stored as methods (lambda-lifted `f@12`), sorted by the
// compiler's stamp, named by ChooseUniqueName (`x`, `x0`, …). A source variable is identified by
// its name and its order among the member's bindings of that name (a quotation's variables carry
// no position, so this is what the run time can find again); one of the optimizer's temporaries
// (`x_0`, a split tuple's element) by its tuple's identity and the element, the tuple being the
// source variable whose value holds the temporary's source range.
//
//   dotnet fsi capturemap.fsx <fsc args file, one per line> <project dir> <output map>
//
// Map lines (tab-separated): `B file line` starts a block; then `V field name ordinal` or
// `E field name ordinal element`, and `U name ordinal element` for a split tuple's element nothing
// in the optimized member keeps (unused). A block any of whose fields is not attributed is left out: it
// keeps the strict behaviour.

#r "nuget: FSharp.Compiler.Service, 43.12.400"

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text

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

let argsFile, projectDir, output = fsi.CommandLineArgs.[1], fsi.CommandLineArgs.[2], fsi.CommandLineArgs.[3]
let args = File.ReadAllLines argsFile
let isSource (a: string) = not (a.StartsWith "-") && (a.EndsWith ".fs" || a.EndsWith ".fsi")
let checker = FSharpChecker.Create(keepAssemblyContents = true)
let options =
    { checker.GetProjectOptionsFromCommandLineArgs(Path.Combine(projectDir, "project.fsproj"), args |> Array.filter (isSource >> not)) with
        SourceFiles = args |> Array.filter isSource |> Array.map (fun f -> Path.GetFullPath(Path.Combine(projectDir, f))) }
let results = checker.ParseAndCheckProject options |> Async.RunSynchronously
let errors = results.Diagnostics |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
if errors.Length > 0 then failwithf "the project does not check: %A" (Array.truncate 3 errors)
let unoptimized = results.AssemblyContents
let optimized = results.GetOptimizedAssemblyContents()

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
/// (the lowering inlines it); a lambda-lifted value (`f@12`) is a method, not captured.
let fieldsOf (definitions: IDictionary<int64, FSharpExpr>) (machine: FSharpExpr) =
    let seen = HashSet<int64>()
    let rec expand (vs: FSharpMemberOrFunctionOrValue list) =
        [ for v in vs do
            if seen.Add(stamp v) then
                match definitions.TryGetValue(stamp v) with
                | true, d when isResumable v -> yield! expand (freeLocals d)
                | _ when v.LogicalName.Contains "@" -> ()
                | _ -> yield v ]
    let vs = expand (freeLocals machine) |> List.sortBy stamp
    let names, _ = ((Set.empty, []), vs) ||> List.fold (fun (taken, acc) v -> let n = chooseUnique v.LogicalName taken in taken.Add n, n :: acc) |> fun (t, acc) -> List.rev acc, t
    List.zip names vs

/// The block's file and line: the caller information Run hands DlrRun.Machine.
let blockKey (machine: FSharpExpr) =
    let rec find (e: FSharpExpr) =
        match e with
        | FSharpExprPatterns.Call(_, mfv, _, _, args) when mfv.CompiledName = "Machine" && (try mfv.DeclaringEntity.Value.CompiledName = "DlrRun" with _ -> false) ->
            match List.rev args with
            | FSharpExprPatterns.Const(:? int as line, _) :: FSharpExprPatterns.Const(:? string as file, _) :: _ -> Some(file, line)
            | _ -> None
        | _ -> e.ImmediateSubExpressions |> List.tryPick find
    find machine

// ---- Identities, from the member's unoptimized tree: a variable's name and order among the
// member's bindings of that name, as a quotation of the member lays them out (parameters first,
// then pre-order; a let's value before its variable).

/// A value found again in another tree: FCS copies the tree it optimizes (new stamps), keeping
/// each value's name and declaration.
let sourceKey (v: FSharpMemberOrFunctionOrValue) = v.LogicalName, v.DeclarationLocation.ToString()

/// A curried parameter of tuple type compiles as one parameter per element (`x_0`, `x_1`), and
/// FCS shows them so; the quotation has the one tuple `x`. Such a group: (`x`, its elements).
let splitParameter (group: FSharpMemberOrFunctionOrValue list) =
    let parts =
        group |> List.mapi (fun i v ->
            let n = v.LogicalName
            let at = n.LastIndexOf '_'
            if at > 0 && n.Substring(at + 1) = string i then Some(n.Substring(0, at)) else None)
    match parts with
    | Some b :: _ when group.Length > 1 && List.forall ((=) (Some b)) parts -> Some b
    | _ -> None

/// Each binding's construct (the `let`, or the application of a lambda applied on the spot):
/// a temporary the optimizer makes of the whole binding has its range.
let bindRanges = Dictionary<string * string, range>()

/// Split parameters' elements: (name, declaration) → (tuple parameter's name, element).
let splitElements = Dictionary<string * string, string * int>()

let bindings (args: FSharpMemberOrFunctionOrValue list list) (body: FSharpExpr) =
    let order = ResizeArray<FSharpMemberOrFunctionOrValue * FSharpExpr option>()
    for group in args do
        match splitParameter group with
        | Some name ->
            // Bound once, as the quotation's tuple parameter: its first element stands for it.
            order.Add((group.Head, None))
            group |> List.iteri (fun i v -> splitElements.[sourceKey v] <- (name, i))
        | None -> for v in group do order.Add((v, None))
    // The body may rebuild a split parameter's tuple (`let x = (x_0, x_1)`): that is the parameter
    // itself, not a binding of its own.
    let rebuilt = HashSet<string * string>()
    let rec go (e: FSharpExpr) =
        match e with
        | FSharpExprPatterns.Let((v, (FSharpExprPatterns.NewTuple(_, es) as d), _), b)
            when not es.IsEmpty
                 && es |> List.forall (function
                    | FSharpExprPatterns.Value w -> (match splitElements.TryGetValue(sourceKey w) with | true, (n, _) -> n = v.LogicalName | _ -> false)
                    | _ -> false) ->
            rebuilt.Add(sourceKey v) |> ignore
            order.Add((v, Some d)); go b
        | FSharpExprPatterns.Let((v, d, _), b) -> go d; bindRanges.[sourceKey v] <- e.Range; order.Add((v, Some d)); go b
        | FSharpExprPatterns.LetRec(bs, b) -> (for (v, d, _) in bs do order.Add((v, Some d))); (for (_, d, _) in bs do go d); go b
        // A lambda applied on the spot: its parameter's value is the argument. As a quotation
        // has it: the parameter, its body, then the argument.
        | FSharpExprPatterns.Application(FSharpExprPatterns.Lambda(v, b), _, [ arg ]) -> bindRanges.[sourceKey v] <- e.Range; order.Add((v, Some arg)); go b; go arg
        | FSharpExprPatterns.Lambda(v, b) -> order.Add((v, None)); go b
        | FSharpExprPatterns.TryWith(b, v1, f, v2, h, _, _) -> go b; order.Add((v1, None)); go f; order.Add((v2, None)); go h
        | FSharpExprPatterns.DecisionTree(d, targets) -> go d; for (vs, t) in targets do (for v in vs do order.Add((v, None))); go t
        | _ -> for x in e.ImmediateSubExpressions do go x
    go body
    let counts = Dictionary<string, int>()
    let ordinals = Dictionary<string * string, int>()
    let entries =
        [ for (v, d) in order ->
            let name = match splitElements.TryGetValue(sourceKey v) with | true, (b, _) -> b | _ -> v.LogicalName
            if rebuilt.Contains(sourceKey v) then
                // The split parameter of that name, bound last.
                sourceKey v, (v, counts.[name] - 1, d)
            else
            let n = match counts.TryGetValue name with | true, n -> n | _ -> 0
            counts.[name] <- n + 1
            ordinals.[(name, v.DeclarationLocation.ToString())] <- n
            sourceKey v, (v, n, d) ]
    // The other elements of a split parameter share its ordinal.
    let extra =
        [ for group in args do
            match splitParameter group with
            | Some name ->
                let n = ordinals.[(name, group.Head.DeclarationLocation.ToString())]
                for v in group.Tail -> sourceKey v, (v, n, None)
            | None -> () ]
    dict (entries @ extra)

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

// ---- The map.

let lines = ResizeArray<string>()
let mutable blocks, mapped = 0, 0
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
            let bySource = bindings uargs ubody
            let rec machines (e: FSharpExpr) =
                match e with
                | FSharpExprPatterns.Call(_, mfv, _, _, _) when mfv.CompiledName = "__stateMachine" ->
                    blocks <- blocks + 1
                    match blockKey e with
                    | None -> ()
                    | Some(file, line) ->
                        // A value of the optimized member: the source variable (name, ordinal) it is,
                        // and the element when it is one of a split tuple's; None when not attributed.
                        let attribute (v: FSharpMemberOrFunctionOrValue) : (string * int * int option) option =
                                match bySource.TryGetValue(sourceKey v) with
                                | true, (_, n, _) when splitElements.ContainsKey(sourceKey v) ->
                                    let name, i = splitElements.[sourceKey v]
                                    Some(name, n, Some i)
                                | true, (sv, n, _) -> Some(sv.LogicalName, n, None)
                                | _ ->
                                    // The optimizer's own: a temporary `<tuple>_<i>` (an element), or a value
                                    // it re-created under the source name (a pattern's). Attributed to the one
                                    // source variable of that name whose value holds its range, whose definition
                                    // has its range, or whose own binding it starts at (the branching
                                    // rewrite's `$tupleElem`); else none.
                                    let r = v.DeclarationLocation
                                    let owner (name: string) (fits: FSharpMemberOrFunctionOrValue -> bool) =
                                        let candidates =
                                            bySource.Values |> Seq.filter (fun (sv, _, _) -> sv.LogicalName = name && fits sv) |> List.ofSeq
                                        // The binding the optimizer made the temporary of, whole: exactly one
                                        // whose construct has its range; else by value, below.
                                        match candidates |> List.filter (fun (sv, _, _) -> match bindRanges.TryGetValue(sourceKey sv) with | true, br -> br = r | _ -> false) with
                                        | [ one ] -> [ one ]
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
                                        match owner tuple elementOf with
                                        | [ (sv, n, _) ] -> Some(sv.LogicalName, n, Some i)
                                        | _ -> None
                                    | None ->
                                        match owner v.LogicalName (fun sv -> sv.FullType.Format(FSharpDisplayContext.Empty) = v.FullType.Format(FSharpDisplayContext.Empty)) with
                                        | [ (sv, n, _) ] -> Some(sv.LogicalName, n, None)
                                        | _ -> None
                        let fields = fieldsOf definitions e
                        let attributed = fields |> List.map (fun (field, v) -> field, attribute v)
                        let entries =
                            attributed |> List.map (fun (field, a) ->
                                match a with
                                | Some(name, n, None) -> Some(sprintf "V\t%s\t%s\t%d" field name n)
                                | Some(name, n, Some i) -> Some(sprintf "E\t%s\t%s\t%d\t%d" field name n i)
                                | None -> None)
                        // A split tuple's element with no field, and no temporary of it read anywhere in the
                        // optimized member: unused. (An element read must be in the machine: a field, or
                        // substituted, which only an effect-free definition is, and the run time takes
                        // that first.)
                        let unused =
                            let elementsOf = Dictionary<string * int, int>()
                            for (sv, n, _) in bySource.Values do
                                if sv.FullType.IsTupleType then elementsOf.[(sv.LogicalName, n)] <- sv.FullType.GenericArguments.Count
                            let split = attributed |> List.choose (function (_, Some(name, n, Some _)) -> Some(name, n) | _ -> None) |> List.distinct
                            let present = HashSet<string * int * int>()
                            let rec all (e: FSharpExpr) =
                                match e with
                                // Read, not merely bound or set: the branching rewrite makes and assigns a
                                // local for every element, read or not.
                                | FSharpExprPatterns.Value v ->
                                    match attribute v with
                                    | Some(name, n, Some i) -> present.Add((name, n, i)) |> ignore
                                    | _ -> ()
                                | _ -> ()
                                for x in e.ImmediateSubExpressions do all x
                            all obody
                            [ for (name, n) in split do
                                match elementsOf.TryGetValue((name, n)) with
                                | true, count ->
                                    for j in 0 .. count - 1 do
                                        if not (present.Contains((name, n, j))) then yield sprintf "U\t%s\t%d\t%d" name n j
                                | _ -> () ]
                        if not (List.forall Option.isSome entries) then
                            for (field, v), entry in List.zip (fieldsOf definitions e) entries do
                                if entry.IsNone then eprintfn "UNATTRIBUTED %s (%s at %s) block %s:%d" field v.LogicalName (v.DeclarationLocation.ToString()) (Path.GetFileName file) line
                        if List.forall Option.isSome entries then
                            mapped <- mapped + 1
                            lines.Add(sprintf "B\t%s\t%d" file line)
                            for e in entries do lines.Add e.Value
                            for u in unused do lines.Add u
                | _ -> for x in e.ImmediateSubExpressions do machines x
            machines obody
        | _ -> ()
for o in optimized.ImplementationFiles do
    members o.Declarations
File.WriteAllLines(output, lines)
eprintfn "%d blocks, %d mapped" blocks mapped
