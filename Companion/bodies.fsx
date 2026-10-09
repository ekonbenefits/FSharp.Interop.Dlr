// Block bodies (spike for the companion's `body` section): converts each member that holds a
// dlr { } block from FCS's unoptimized tree to an `Expr`, through a wire form that names types
// and members the way the run time can resolve them (no reflection on the encoding side), and
// compares the result with the member's [<ReflectedDefinition>] quotation, the oracle.
//
//   dotnet fsi bodies.fsx <fsc args file> <project dir> <built assembly>

#r "nuget: FSharp.Compiler.Service, 43.12.400"
#load "../FSharp.Interop.Dlr/BodyMap.fs"

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Quotations
open FSharp.Interop.Dlr.BodyMap

let argsFile, projectDir, built = fsi.CommandLineArgs.[1], fsi.CommandLineArgs.[2], fsi.CommandLineArgs.[3]
let args = File.ReadAllLines argsFile
let isSource (a: string) = not (a.StartsWith "-") && (a.EndsWith ".fs" || a.EndsWith ".fsi")
let checker = FSharpChecker.Create(keepAssemblyContents = true)
let options =
    { checker.GetProjectOptionsFromCommandLineArgs(Path.Combine(projectDir, "project.fsproj"), args |> Array.filter (isSource >> not)) with
        SourceFiles = args |> Array.filter isSource |> Array.map (fun f -> Path.GetFullPath(Path.Combine(projectDir, f))) }
let sw = Diagnostics.Stopwatch.StartNew()
let results = checker.ParseAndCheckProject options |> Async.RunSynchronously
let errors = results.Diagnostics |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
if errors.Length > 0 then failwithf "the project does not check: %A" (Array.truncate 3 errors)
eprintfn "checked in %O" sw.Elapsed

let builtDir = Path.GetDirectoryName(Path.GetFullPath built)
AppDomain.CurrentDomain.add_AssemblyResolve(ResolveEventHandler(fun _ e ->
    let name = AssemblyName(e.Name).Name + ".dll"
    match Directory.GetFiles(builtDir, name, SearchOption.AllDirectories) with
    | [||] -> null
    | fs -> Assembly.LoadFrom(fs |> Array.sortBy (fun f -> f.Length) |> Array.head)))
let assembly = Assembly.LoadFrom(Path.GetFullPath built)

// ---- Encoding: FCS → wire. Names only.

let rec typeRef (t: FSharpType) : TypeRef =
    if t.IsAbbreviation then typeRef t.AbbreviatedType
    elif t.IsGenericParameter then TParam t.GenericParameter.Name
    elif t.IsFunctionType then TNamed("FSharp.Core", "Microsoft.FSharp.Core.FSharpFunc`2", [ typeRef t.GenericArguments.[0]; typeRef t.GenericArguments.[1] ])
    elif t.IsStructTupleType then TTuple(true, [ for a in t.GenericArguments -> typeRef a ])
    elif t.IsTupleType then TTuple(false, [ for a in t.GenericArguments -> typeRef a ])
    elif t.IsAnonRecordType then
        let d = t.AnonRecordTypeDetails
        TNamed(d.Assembly.SimpleName, d.CompiledName, [ for a in t.GenericArguments -> typeRef a ])
    else
        let e = t.TypeDefinition
        if e.IsArrayType then TArray(e.ArrayRank, typeRef t.GenericArguments.[0])
        elif e.IsByRef then TByref(typeRef t.GenericArguments.[0])
        elif e.IsFSharpAbbreviation then typeRef (e.AbbreviatedType.Instantiate(Seq.zip e.GenericParameters t.GenericArguments |> List.ofSeq))
        else
            let args = [ for a in t.GenericArguments do if not a.IsMeasureType then typeRef a ]
            TNamed(entityAssembly e, entityName e, args)

and entityAssembly (e: FSharpEntity) = e.Assembly.SimpleName

/// The CLI name: namespace, enclosing types joined by `+`, compiled names (a module's `Module` suffix).
and entityName (e: FSharpEntity) : string =
    match e.DeclaringEntity with
    | Some d when not d.IsNamespace -> entityName d + "+" + e.CompiledName
    | _ ->
        match e.Namespace with
        | Some ns -> ns + "." + e.CompiledName
        | None -> e.CompiledName

let rec isMeasure (t: FSharpType) = t.IsMeasureType

let memberRef (m: FSharpMemberOrFunctionOrValue) : MemberRef =
    let declaring =
        match m.DeclaringEntity with
        | Some e -> TNamed(entityAssembly e, entityName e, [])
        | None -> failwithf "no declaring entity: %s" m.LogicalName
    let parameters =
        // An extension member compiles static, the extended value first.
        [ if m.IsExtensionMember && m.IsInstanceMember then
            let e = m.ApparentEnclosingEntity.Value
            yield TNamed(entityAssembly e, entityName e, [ for p in e.GenericParameters -> TParam p.Name ])
          for g in m.CurriedParameterGroups do
            for p in g do
                yield typeRef p.Type ]
        // A function of unit compiles with no parameter.
        |> function
            | [ TNamed(_, "Microsoft.FSharp.Core.Unit", []) ] when not m.IsConstructor || true -> []
            | ps -> ps
    { Declaring = declaring
      Name = m.CompiledName
      Instance = m.IsConstructor || (m.IsInstanceMember && not m.IsExtensionMember)
      // FCS lists the enclosing type's parameters too.
      GenericArity =
        let enclosing = match m.DeclaringEntity with Some e -> [ for p in e.GenericParameters -> p.Name ] | None -> []
        m.GenericParameters |> Seq.filter (fun p -> not (List.contains p.Name enclosing)) |> Seq.length
      Parameters = parameters }

// ---- Members that hold a block: a call to the builder's Run with caller information.

let isRun (m: FSharpMemberOrFunctionOrValue) =
    m.CompiledName = "Run" && (try m.DeclaringEntity.Value.CompiledName = "DlrBuilder" with _ -> false)

let rec holdsBlock (e: FSharpExpr) =
    match e with
    | FSharpExprPatterns.Call(_, m, _, _, _) when isRun m -> true
    | _ -> e.ImmediateSubExpressions |> List.exists holdsBlock

let candidates =
    let found = ResizeArray()
    let rec go (ds: FSharpImplementationFileDeclaration list) =
        for d in ds do
            match d with
            | FSharpImplementationFileDeclaration.Entity(_, sub) -> go sub
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(m, args, body) when holdsBlock body -> found.Add((m, args, body))
            | _ -> ()
    for f in results.AssemblyContents.ImplementationFiles do go f.Declarations
    List.ofSeq found
eprintfn "%d members hold a block" candidates.Length

// ---- The tree: FSharpExpr → wire.

let unsupported = Dictionary<string, int>()
let note (s: string) = unsupported.[s] <- (match unsupported.TryGetValue s with | true, n -> n + 1 | _ -> 0 + 1)

let encode (args: FSharpMemberOrFunctionOrValue list list) (body: FSharpExpr) : W =
    let ids = Dictionary<FSharpMemberOrFunctionOrValue, int>()
    let mutable next = 0
    let def (v: FSharpMemberOrFunctionOrValue) =
        next <- next + 1
        ids.[v] <- next
        { Id = next; Name = v.LogicalName; Type = typeRef v.FullType; Mutable = v.IsMutable }
    let fresh (name: string) (t: TypeRef) =
        next <- next + 1
        { Id = next; Name = name; Type = t; Mutable = false }
    let targetsStack = Stack<(FSharpMemberOrFunctionOrValue list * FSharpExpr) list>()
    /// A curried parameter of tuple type, which FCS shows split (`u_0`, `u_1`): element → (tuple, index).
    let split = Dictionary<FSharpMemberOrFunctionOrValue, VarDef * int>()
    let isSplitOf (name: string) (x: FSharpExpr) =
        match x with
        | FSharpExprPatterns.Value w -> (match split.TryGetValue w with | true, (d, _) -> d.Name = name | _ -> false)
        | _ -> false
    let rec go (e: FSharpExpr) : W =
        match e with
        | FSharpExprPatterns.Value v when split.ContainsKey v -> let d, i = split.[v] in WTupleGet(d.Type, i, WVar d.Id)
        // The body rebuilding the split tuple is the parameter itself.
        | FSharpExprPatterns.Let((v, FSharpExprPatterns.NewTuple(_, (_ :: _ as xs)), _), b) when List.forall (isSplitOf v.LogicalName) xs ->
            let d, _ = (match xs.Head with FSharpExprPatterns.Value w -> split.[w] | _ -> failwith "unreachable")
            ids.[v] <- d.Id
            go b
        | FSharpExprPatterns.Value v when ids.ContainsKey v -> WVar ids.[v]
        | FSharpExprPatterns.Value v when v.IsModuleValueOrMember ->
            WStaticValue(TNamed(entityAssembly v.DeclaringEntity.Value, entityName v.DeclaringEntity.Value, []), v.CompiledName)
        | FSharpExprPatterns.ValueSet(v, x) when ids.ContainsKey v -> WVarSet(ids.[v], go x)
        | FSharpExprPatterns.Lambda(v, b) -> let d = def v in WLambda(d, go b)
        | FSharpExprPatterns.Let((v, x, _), b) ->
            let x = go x
            let d = def v
            WLet(d, x, go b)
        | FSharpExprPatterns.LetRec(bs, b) ->
            let ds = [ for (v, _, _) in bs -> def v ]
            WLetRec([ for d, (_, x, _) in List.zip ds bs -> d, go x ], go b)
        | FSharpExprPatterns.Application(f, _, xs) -> List.fold (fun f x -> WApp(f, go x)) (go f) xs
        | FSharpExprPatterns.Const(v, t) -> WConst(v, typeRef t)
        | FSharpExprPatterns.DefaultValue t -> WDefault(typeRef t)
        | FSharpExprPatterns.NewObject(m, targs, xs) ->
            let r = memberRef m
            let xs = if r.Parameters.IsEmpty then xs |> List.filter (fun x -> typeRef x.Type <> TNamed("FSharp.Core", "Microsoft.FSharp.Core.Unit", [])) else xs
            WNewObject(r, List.map typeRef targs, List.map go xs)
        // A module value: its static property.
        | FSharpExprPatterns.Call(None, m, _, _, []) when not m.IsMember && m.CurriedParameterGroups.Count = 0 && m.GenericParameters.Count = 0 && m.DeclaringEntity.IsSome && m.DeclaringEntity.Value.IsFSharpModule ->
            WStaticValue(TNamed(entityAssembly m.DeclaringEntity.Value, entityName m.DeclaringEntity.Value, []), m.CompiledName)
        | FSharpExprPatterns.ILFieldGet(o, t, name) -> WFieldGet(Option.map go o, typeRef t, name)
        | FSharpExprPatterns.ILFieldSet(o, t, name, x) -> WFieldSet(Option.map go o, typeRef t, name, go x)
        | FSharpExprPatterns.AnonRecordGet(x, t, i) -> WFieldGet(Some(go x), typeRef t, t.AnonRecordTypeDetails.SortedFieldNames.[i])
        | FSharpExprPatterns.AddressOf x -> go x
        | FSharpExprPatterns.CallWithWitnesses(o, m, targs, margs, ws, xs) when not ws.IsEmpty ->
            let r = memberRef m
            let o, xs = if m.IsExtensionMember then None, Option.toList o @ xs else o, xs
            WCallW(Option.map go o, r, List.map typeRef targs, List.map typeRef margs, List.map go ws, List.map go xs)
        | FSharpExprPatterns.Call(o, m, targs, margs, xs) ->
            let r = memberRef m
            let xs = if r.Parameters.IsEmpty then xs |> List.filter (fun x -> typeRef x.Type <> TNamed("FSharp.Core", "Microsoft.FSharp.Core.Unit", [])) else xs
            // An extension member's target is its first argument.
            let o, xs = if m.IsExtensionMember then None, Option.toList o @ xs else o, xs
            WCall(Option.map go o, r, List.map typeRef targs, List.map typeRef margs, List.map go xs)
        | FSharpExprPatterns.NewRecord(t, xs) -> WNewRecord(typeRef t, List.map go xs)
        | FSharpExprPatterns.NewAnonRecord(t, xs) -> WNewRecord(typeRef t, List.map go xs)
        | FSharpExprPatterns.NewUnionCase(t, c, xs) -> WNewUnion(typeRef t, c.CompiledName, List.map go xs)
        | FSharpExprPatterns.UnionCaseTest(x, t, c) -> WUnionTest(go x, typeRef t, c.CompiledName)
        | FSharpExprPatterns.UnionCaseGet(x, t, c, f) ->
            WUnionGet(go x, typeRef t, c.CompiledName, c.Fields |> Seq.findIndex (fun g -> g.Name = f.Name))
        | FSharpExprPatterns.NewTuple(t, xs) -> WNewTuple(typeRef t, List.map go xs)
        | FSharpExprPatterns.TupleGet(t, i, x) -> WTupleGet(typeRef t, i, go x)
        | FSharpExprPatterns.FSharpFieldGet(o, t, f) -> WFieldGet(Option.map go o, typeRef t, f.Name)
        | FSharpExprPatterns.FSharpFieldSet(o, t, f, x) -> WFieldSet(Option.map go o, typeRef t, f.Name, go x)
        | FSharpExprPatterns.IfThenElse(c, a, b) -> WIf(go c, go a, go b)
        | FSharpExprPatterns.Sequential(a, b) -> WSeq(go a, go b)
        | FSharpExprPatterns.WhileLoop(c, b, _) -> WWhile(go c, go b)
        | FSharpExprPatterns.FastIntegerForLoop(lo, hi, FSharpExprPatterns.Lambda(v, b), true, _, _) ->
            let lo, hi = go lo, go hi
            let d = def v
            WFor(d, lo, hi, go b)
        | FSharpExprPatterns.TryWith(b, fv, f, cv, c, _, _) ->
            let b = go b
            let fd = def fv
            let f = go f
            let cd = def cv
            WTryWith(b, fd, f, cd, go c)
        | FSharpExprPatterns.TryFinally(b, f, _, _) -> WTryFinally(go b, go f)
        | FSharpExprPatterns.Coerce(t, x) -> WCoerce(typeRef t, go x)
        | FSharpExprPatterns.TypeTest(t, x) -> WTypeTest(typeRef t, go x)
        | FSharpExprPatterns.NewArray(t, xs) -> WNewArray(typeRef t, List.map go xs)
        | FSharpExprPatterns.NewDelegate(t, x) ->
            // As many lambdas as Invoke takes parameters; a delegate of none has the unit lambda's.
            let arity =
                t.TypeDefinition.MembersFunctionsAndValues |> Seq.find (fun m -> m.CompiledName = "Invoke")
                |> fun m -> m.CurriedParameterGroups |> Seq.sumBy (fun g -> g.Count)
            let rec lambdas n acc x =
                match x with
                | FSharpExprPatterns.Lambda(v, b) when n > 0 -> lambdas (n - 1) (v :: acc) b
                | b -> List.rev acc, b
            let vs, b = lambdas (max 1 arity) [] x
            let ds = List.map def vs
            WNewDelegate(typeRef t, ds, go b)
        | FSharpExprPatterns.Quote x -> WQuote(go x)
        // A trait call in a witness: the member the constraint resolves to, a plain call.
        | FSharpExprPatterns.TraitCall(sources, name, flags, _, argTypes, xs) ->
            let xs = List.map go xs
            let o, xs = if flags.IsInstance then Some xs.Head, xs.Tail else None, xs
            WTraitCall(typeRef sources.Head, name, o, [ for t in argTypes -> typeRef t ], xs)
        | FSharpExprPatterns.DecisionTree(d, targets) ->
            targetsStack.Push targets
            let r = go d
            targetsStack.Pop() |> ignore
            r
        | FSharpExprPatterns.DecisionTreeSuccess(i, xs) ->
            let targets = targetsStack.Pop()
            let vs, b = targets.[i]
            let xs = List.map go xs
            targetsStack.Push targets
            // Each use of a target is its own copy, as the quotation has it.
            let ds = List.map def vs
            let b = go b
            List.foldBack2 (fun d x b -> WLet(d, x, b)) ds xs b
        | FSharpExprPatterns.DebugPoint(_, x) -> go x
        | _ ->
            let kind = (sprintf "%A" e).Split([| '(' ; ' '; '\n' |]).[0]
            note kind
            WUnsupported kind
    // The member's parameters, as the quotation of a member binds them.
    let rec wrap (groups: FSharpMemberOrFunctionOrValue list list) =
        match groups with
        | [] -> go body
        | [ v ] :: rest -> let d = def v in WLambda(d, wrap rest)
        | [] :: rest -> WLambda(fresh "unitVar" (TNamed("FSharp.Core", "Microsoft.FSharp.Core.Unit", [])), wrap rest)
        | group :: rest when
            group.Length > 1
            && group |> List.mapi (fun i (v: FSharpMemberOrFunctionOrValue) -> v.LogicalName.EndsWith("_" + string i)) |> List.forall id
            && (group |> List.map (fun v -> v.LogicalName.Substring(0, v.LogicalName.LastIndexOf '_')) |> List.distinct |> List.length) = 1 ->
            let name = group.Head.LogicalName.Substring(0, group.Head.LogicalName.LastIndexOf '_')
            let t = TTuple(false, [ for v in group -> typeRef v.FullType ])
            let d = fresh name t
            group |> List.iteri (fun i v -> split.[v] <- (d, i))
            WLambda(d, wrap rest)
        | group :: rest ->
            let t = TTuple(false, [ for v in group -> typeRef v.FullType ])
            let tupled = fresh "tupledArg" t
            let ds = List.map def group
            WLambda(tupled, List.foldBack (fun (i, d) b -> WLet(d, WTupleGet(t, i, WVar tupled.Id), b)) (List.indexed ds) (wrap rest))
    wrap args

// ---- The oracle: the member's reflected definition, compared modulo variable identity.

let methodFor (m: FSharpMemberOrFunctionOrValue) : MethodBase option =
    try
        let declaring = assembly.GetType(entityName m.DeclaringEntity.Value)
        let ms = Seq.append (declaring.GetMethods all |> Seq.cast<MethodBase>) (declaring.GetConstructors all |> Seq.cast<MethodBase>)
        ms |> Seq.filter (fun c -> c.Name = m.CompiledName) |> List.ofSeq |> function [ c ] -> Some c | _ -> None
    with _ -> None

/// `b` with its bound variables replaced by `a`'s where the two trees line up.
let rec align (env: Dictionary<Var, Var>) (a: Expr) (b: Expr) : Expr =
    match a, b with
    | ExprShape.ShapeVar _, ExprShape.ShapeVar vb -> (match env.TryGetValue vb with | true, v -> Expr.Var v | _ -> b)
    | ExprShape.ShapeLambda(va, ba), ExprShape.ShapeLambda(vb, bb) ->
        if va.Type = vb.Type && va.IsMutable = vb.IsMutable then env.[vb] <- va
        let v = match env.TryGetValue vb with | true, v -> v | _ -> vb
        Expr.Lambda(v, align env ba bb)
    | Patterns.VarSet(_, xa), Patterns.VarSet(vb, xb) ->
        Expr.VarSet((match env.TryGetValue vb with | true, v -> v | _ -> vb), align env xa xb)
    | ExprShape.ShapeCombination(_, [ xa ]), Patterns.NewDelegate(t, _, _) & ExprShape.ShapeCombination(_, [ xb ]) ->
        rawNewDelegate t (align env xa xb)
    | ExprShape.ShapeCombination(_, xa), ExprShape.ShapeCombination(ob, xb) when xa.Length = xb.Length ->
        ExprShape.RebuildShapeCombination(ob, List.map2 (align env) xa xb)
    | _ -> b

/// The first place the trees differ: (expected, actual), smallest.
let rec firstDiff (a: Expr) (b: Expr) : (Expr * Expr) option =
    if a = b then None
    else
        match a, b with
        | ExprShape.ShapeLambda(va, ba), ExprShape.ShapeLambda(vb, bb) when obj.ReferenceEquals(va, vb) -> firstDiff ba bb
        | ExprShape.ShapeLambda(va, _), ExprShape.ShapeLambda(vb, _) ->
            Some(Expr.Value(sprintf "var %s: %s%s" va.Name va.Type.Name (if va.IsMutable then " mutable" else "")), Expr.Value(sprintf "var %s: %s%s" vb.Name vb.Type.Name (if vb.IsMutable then " mutable" else "")))
        | ExprShape.ShapeCombination(oa, xa), ExprShape.ShapeCombination(_, xb) when xa.Length = xb.Length ->
            let same =
                match a with
                | Patterns.NewDelegate(t, _, _) -> (match b with Patterns.NewDelegate(u, _, _) -> t = u | _ -> false)
                | _ -> try ExprShape.RebuildShapeCombination(oa, xb) = b with _ -> false
            if same then List.zip xa xb |> List.tryPick (fun (x, y) -> firstDiff x y)
            else Some(a, b)
        | _ -> Some(a, b)

let short (e: Expr) = let s = (sprintf "%A" e).Replace("\n", " ") in if s.Length > 160 then s.Substring(0, 160) + "…" else s

let mutable same, differ, failed, noOracle = 0, 0, 0, 0
let reasons = Dictionary<string, int>()
let reason (s: string) = reasons.[s] <- (match reasons.TryGetValue s with | true, n -> n + 1 | _ -> 1)
let samples = Dictionary<string, string>()
for (m, args, body) in candidates do
    match methodFor m with
    | None -> noOracle <- noOracle + 1; eprintfn "no method: %s.%s" (try entityName m.DeclaringEntity.Value with _ -> "?") m.CompiledName
    | Some mb ->
        match (try Expr.TryGetReflectedDefinition mb with _ -> None) with
        | None -> noOracle <- noOracle + 1; eprintfn "no definition: %O" mb
        | Some expected ->
            try
                let scope = Dictionary<string, Type>()
                if mb.IsGenericMethod then for t in mb.GetGenericArguments() do scope.[t.Name] <- t
                if mb.DeclaringType.IsGenericType then for t in mb.DeclaringType.GetGenericArguments() do scope.[t.Name] <- t
                let w = encode args body
                let context = TNamed(entityAssembly m.DeclaringEntity.Value, entityName m.DeclaringEntity.Value, [])
                let context', w' = decodeEntry (encodeEntry context w)
                if w' <> w || context' <> context then failwith "text round trip"
                let actual = decode scope w'
                let aligned = align (Dictionary(HashIdentity.Reference)) expected actual
                match firstDiff expected aligned with
                | None -> same <- same + 1
                | Some(x, y) ->
                    differ <- differ + 1
                    let key = sprintf "%s vs %s" ((sprintf "%A" x).Split([| '('; ' '; '\n' |]).[0]) ((sprintf "%A" y).Split([| '('; ' '; '\n' |]).[0])
                    reason key
                    let detail (e: Expr) =
                        match e with
                        | Patterns.CallWithWitnesses(_, mi, miw, ws, _) -> sprintf " [witnesses %s %d]" miw.Name ws.Length
                        | Patterns.Call(_, mi, _) -> sprintf " [%O]" mi
                        | _ -> ""
                    if not (samples.ContainsKey key) then samples.[key] <- sprintf "%s\n      expected %s%s\n      actual   %s%s" m.CompiledName (short x) (detail x) (short y) (detail y)
            with e ->
                failed <- failed + 1
                let msg = match e with Unsupported s -> "unsupported: " + s | e -> e.GetType().Name + ": " + e.Message
                let key = msg.Split([|"\n"|], StringSplitOptions.None).[0] |> fun s -> if s.Length > 400 then s.Substring(0, 400) else s
                reason key
                if not (samples.ContainsKey key) then samples.[key] <- m.CompiledName + (if msg.Contains "delegate Action" || msg.Contains "TraitCall" then "\n      expected " + (sprintf "%A" expected) else "")

printfn "members %d: same %d, differ %d, failed %d, no oracle %d" candidates.Length same differ failed noOracle
for KeyValue(k, n) in reasons |> Seq.sortByDescending (fun kv -> kv.Value) |> Seq.truncate 25 do
    printfn "%5d  %s\n      e.g. %s" n k samples.[k]
if unsupported.Count > 0 then printfn "unsupported FCS nodes: %A" (List.ofSeq unsupported)

// ---- The map: a `body` section for every block, merged into the capture map when one is given.
//   dotnet fsi bodies.fsx <fsc args> <project dir> <built assembly> <output map> [capture map]

if fsi.CommandLineArgs.Length > 4 then
    let output = fsi.CommandLineArgs.[4]
    let blocks = Dictionary<string, ResizeArray<string>>()
    let order = ResizeArray<string>()
    let block (key: string) =
        match blocks.TryGetValue key with
        | true, b -> b
        | _ ->
            let b = ResizeArray [ "B\t" + key ]
            blocks.[key] <- b
            order.Add key
            b
    if fsi.CommandLineArgs.Length > 5 then
        let mutable current = None
        for line in File.ReadAllLines fsi.CommandLineArgs.[5] |> Array.skip 1 do
            if line.StartsWith "B\t" then current <- Some(block (line.Substring 2))
            else current.Value.Add line
    let mutable written, skipped = 0, 0
    for (m, args, body) in candidates do
        let rec runs (e: FSharpExpr) =
            [ match e with
              | FSharpExprPatterns.Call(_, r, _, _, xs) when isRun r ->
                  match List.rev xs with
                  | FSharpExprPatterns.Const(:? int as line, _) :: FSharpExprPatterns.Const(:? string as file, _) :: _ -> yield file, line
                  | _ -> ()
              | _ -> ()
              for x in e.ImmediateSubExpressions do yield! runs x ]
        let w = encode args body
        let rec unsupportedIn (w: W) = match w with WUnsupported _ -> true | _ -> (sprintf "%A" w).Contains "WUnsupported"
        if unsupportedIn w then skipped <- skipped + 1
        else
            let payload = encodeEntry (TNamed(entityAssembly m.DeclaringEntity.Value, entityName m.DeclaringEntity.Value, [])) w
            // The member once, at its first block; its other blocks refer to that one.
            match runs body |> List.distinct with
            | [] -> ()
            | (file, line) :: rest ->
                let b = block (sprintf "%s\t%d" file line)
                b.Add "S\tbody"
                b.Add("X\t" + payload)
                written <- written + 1
                for (f, l) in rest do
                    let b = block (sprintf "%s\t%d" f l)
                    b.Add "S\tbody"
                    b.Add(sprintf "Y\t%s\t%d" file line)
                    written <- written + 1
    File.WriteAllLines(output, Seq.append [ "DLRMAP\t1" ] (order |> Seq.collect (fun k -> blocks.[k])))
    eprintfn "map: %d block bodies written, %d members skipped (unsupported nodes)" written skipped
