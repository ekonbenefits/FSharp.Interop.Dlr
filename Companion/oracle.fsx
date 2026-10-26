// The body pass's oracle (#219): encodes each member that holds a dlr { } block as the build
// companion does (FSharp.Interop.Dlr.Build/Bodies.fs), decodes it as the run time does (BodyMap.fs),
// through the map's text, and compares the result with the member's [<ReflectedDefinition>]
// quotation, modulo variable identity. Every member with a reflected definition should be the same.
//
//   dotnet fsi oracle.fsx <fsc args file> <project dir> <built assembly>

#r "nuget: FSharp.Compiler.Service, 43.12.400"
#load "../FSharp.Interop.Dlr/BodyMap.fs" "../FSharp.Interop.Dlr.Build/Check.fs" "../FSharp.Interop.Dlr.Build/Fields.fs" "../FSharp.Interop.Dlr.Build/Bodies.fs"

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open FSharp.Compiler.Symbols
open FSharp.Quotations
open FSharp.Interop.Dlr.BodyMap
open FSharp.Interop.Dlr.Build
open FSharp.Interop.Dlr.Build.Bodies

let argsFile, projectDir, built = fsi.CommandLineArgs.[1], fsi.CommandLineArgs.[2], fsi.CommandLineArgs.[3]
let results = Check.project argsFile projectDir
// As the companion: optimized before either tree is walked (the optimizer renames shared values,
// as fsc's reflected definitions have them; Program.fs).
let _optimized = results.GetOptimizedAssemblyContents()
// DLR_ORACLE_ONLY=<text>: only the members whose compiled name contains it (to read one form's samples).
let candidates =
    match Environment.GetEnvironmentVariable "DLR_ORACLE_ONLY" with
    | null | "" -> members results.AssemblyContents
    | only -> members results.AssemblyContents |> List.filter (fun (m, _, _) -> m.CompiledName.Contains only)

let builtDir = Path.GetDirectoryName(Path.GetFullPath built)
AppDomain.CurrentDomain.add_AssemblyResolve(ResolveEventHandler(fun _ e ->
    let name = AssemblyName(e.Name).Name + ".dll"
    match Directory.GetFiles(builtDir, name, SearchOption.AllDirectories) with
    | [||] -> null
    | fs -> Assembly.LoadFrom(fs |> Array.sortBy (fun f -> f.Length) |> Array.head)))
let assembly = Assembly.LoadFrom(Path.GetFullPath built)

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
                let scope = Scope(null)
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
                let key = msg.Split([|"\n"|], StringSplitOptions.None).[0] |> fun s -> if s.Length > 1500 then s.Substring(0, 1500) else s
                reason key
                if not (samples.ContainsKey key) then samples.[key] <- m.CompiledName + (if msg.Contains "delegate Action" || msg.Contains "TraitCall" then "\n      expected " + (sprintf "%A" expected) else "")

printfn "members %d: same %d, differ %d, failed %d, no oracle %d" candidates.Length same differ failed noOracle
for KeyValue(k, n) in reasons |> Seq.sortByDescending (fun kv -> kv.Value) |> Seq.truncate 25 do
    printfn "%5d  %s\n      e.g. %s" n k samples.[k]
if unsupported.Count > 0 then printfn "unsupported FCS nodes: %A" (List.ofSeq unsupported)

// ---- Resolution refuses rather than guesses (#225): a lone candidate of the right name and shape
// whose signature differs (as when a referenced assembly differs at run time) is not bound.
let refused (m: MemberRef) =
    try methodOf (Scope(null)) m [] [] |> ignore; false with Unsupported _ -> true
let int32 = TNamed("System.Runtime", "System.Int32", [])
let mathAbs = { Declaring = TNamed("System.Runtime", "System.Math", []); Name = "Abs"; Instance = false; GenericArity = 0; Parameters = [ int32 ]; Return = int32 }
let exactOk = not (refused mathAbs)
let wrongParam = refused { mathAbs with Parameters = [ TNamed("System.Runtime", "System.String", []) ] }
let wrongReturn = refused { mathAbs with Return = TNamed("System.Runtime", "System.Int64", []) }
// Path.GetTempPath has one overload: right name and shape, wrong return type.
let lone = refused { Declaring = TNamed("System.Runtime", "System.IO.Path", []); Name = "GetTempPath"; Instance = false; GenericArity = 0; Parameters = []; Return = int32 }
// A type of the right full name in another assembly is another type (#259's review): the
// DefaultAsyncBuilder getter, its return type claimed in netstandard rather than FSharp.Core.
let asyncBuilder asm = TNamed(asm, "Microsoft.FSharp.Control.FSharpAsyncBuilder", [])
let defaultBuilder = { Declaring = TNamed("FSharp.Core", "Microsoft.FSharp.Core.ExtraTopLevelOperators", []); Name = "get_DefaultAsyncBuilder"; Instance = false; GenericArity = 0; Parameters = []; Return = asyncBuilder "FSharp.Core" }
let otherAssembly = not (refused defaultBuilder) && refused { defaultBuilder with Return = asyncBuilder "netstandard" }
printfn "signature checks: exact binds %b, wrong parameter refused %b, wrong return refused %b, lone candidate refused %b, same name in another assembly refused %b" exactOk wrongParam wrongReturn lone otherAssembly

// ---- The layout oracle: the capture map names a variable by its name and its order among the
// member's bindings of that name, as the field pass lays the member out (`Fields.bindings`); the
// run time finds it again in the quotation (`Translate.identities`). Per name, the two sequences of
// bindings must agree, each as (mutable?, type): a construct the field pass lays out otherwise (an
// or-pattern's case, a let rec) shows as a count or an order that differs.

/// Tuples, value tuples and anonymous records by kind: their names differ by arity and stamp.
let normal (name: string) =
    if name.StartsWith "ValueTuple`" then "ValueTuple"
    elif name.StartsWith "Tuple`" then "Tuple"
    elif name.StartsWith "<>f__AnonymousType" then "<anon>"
    else name
/// A type's short name, alike on both sides: FCS's erased type, .NET's type.
let rec fcsKey (t: FSharpType) : string = normal (fcsKey' t)
and fcsKey' (t: FSharpType) : string =
    let t = try t.ErasedType with _ -> t
    if t.IsGenericParameter then "'"
    elif t.IsFunctionType then "FSharpFunc`2"
    elif t.IsAnonRecordType then "<anon>"
    elif t.IsStructTupleType then "ValueTuple"
    elif t.IsTupleType then "Tuple"
    elif t.HasTypeDefinition && t.TypeDefinition.IsArrayType then "[]"
    elif t.HasTypeDefinition && t.TypeDefinition.IsByRef then "&"
    elif t.HasTypeDefinition then t.TypeDefinition.CompiledName + (if t.GenericArguments.Count > 0 && not (t.TypeDefinition.CompiledName.Contains "`") then sprintf "`%d" t.GenericArguments.Count else "")
    else "?"
let netKey (t: Type) : string =
    if t.IsGenericParameter then "'"
    elif t.IsArray then "[]"
    elif t.IsByRef then "&"
    elif t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith "FSharpFunc" then "FSharpFunc`2"
    elif t.Name.StartsWith "<>f__AnonymousType" then "<anon>"
    elif t.Name.StartsWith "ValueTuple`" then "ValueTuple"
    elif t.Name.StartsWith "Tuple`" then "Tuple"
    else t.Name
let quotationLayout (e: Expr) =
    let out = ResizeArray<string * (bool * string)>()
    let rec go (e: Expr) =
        match e with
        | ExprShape.ShapeVar _ -> ()
        | ExprShape.ShapeLambda(v, b) -> out.Add((v.Name, (v.IsMutable, netKey v.Type))); go b
        | ExprShape.ShapeCombination(_, es) -> for x in es do go x
    go e
    out |> Seq.groupBy fst |> Seq.map (fun (n, xs) -> n, (xs |> Seq.map snd |> List.ofSeq)) |> Map.ofSeq
let companionLayout args body =
    let _, _, layout, _ = Fields.bindings args body
    // A split tuple parameter's element stands for the tuple (`x_0` for `x`).
    layout |> Seq.groupBy fst |> Seq.map (fun (n, xs) -> n, (xs |> Seq.map (fun (n, v) ->
        let split = v.LogicalName.StartsWith(n + "_") && v.LogicalName.Substring(n.Length + 1) |> Seq.forall Char.IsDigit
        // `tupledArg`: a tupled group's one parameter, counted at its first element.
        if n = "tupledArg" && v.LogicalName <> "tupledArg" then false, "Tuple"
        else v.IsMutable, (if split then "Tuple" else fcsKey v.FullType)) |> List.ofSeq)) |> Map.ofSeq
let mutable layoutSame, layoutDiffer, layoutFailed, layoutReordered = 0, 0, 0, 0
let reorderedSamples = ResizeArray<string>()
let countKinds = Dictionary<string, int>()
let layoutSamples = ResizeArray<string>()
for (m, args, body) in candidates do
    match methodFor m |> Option.bind (fun mb -> try Expr.TryGetReflectedDefinition mb with _ -> None) with
    | None -> ()
    | Some expected ->
        try
            let q, c = quotationLayout expected, companionLayout args body
            let names = Set.union (Set.ofSeq q.Keys) (Set.ofSeq c.Keys)
            let qs n = q.TryFind n |> Option.defaultValue []
            let cs n = c.TryFind n |> Option.defaultValue []
            // The same count in another order is what the run time cannot see; a count that differs
            // it refuses (the map's N line).
            let reordered = names |> Seq.filter (fun n -> qs n <> cs n && (qs n).Length = (cs n).Length) |> List.ofSeq
            if not reordered.IsEmpty then
                layoutReordered <- layoutReordered + 1
                for n in reordered do reorderedSamples.Add(sprintf "%s: '%s' quotation %A, companion %A" m.CompiledName n (qs n) (cs n))
            for n in names do
                if (qs n).Length <> (cs n).Length then
                    let kind = sprintf "count %s: quotation %d, companion %d" (if n.StartsWith "_arg" then "_argN" elif n = "tupledArg" then "tupledArg" else "other") (qs n).Length (cs n).Length
                    let kind = if kind.Contains "other" then kind + " ('" + n + "')" else kind.Split(':').[0]
                    countKinds.[kind] <- (match countKinds.TryGetValue kind with | true, k -> k + 1 | _ -> 1)
            let differing =
                names |> Seq.filter (fun n -> q.TryFind n <> c.TryFind n)
                |> Seq.map (fun n -> sprintf "'%s' quotation %A, companion %A" n (q.TryFind n |> Option.defaultValue []) (c.TryFind n |> Option.defaultValue []))
                |> List.ofSeq
            if differing.IsEmpty then layoutSame <- layoutSame + 1
            else
                layoutDiffer <- layoutDiffer + 1
                if layoutSamples.Count < 12 then layoutSamples.Add(sprintf "%s: %s" m.CompiledName (String.concat "; " differing))
        with e ->
            layoutFailed <- layoutFailed + 1
            if layoutSamples.Count < 12 then layoutSamples.Add(sprintf "%s: %s %s" m.CompiledName (e.GetType().Name) e.Message)
printfn "layouts %d: same %d, differ %d (same count, another order: %d), failed %d" (layoutSame + layoutDiffer + layoutFailed) layoutSame layoutDiffer layoutReordered layoutFailed
for l in reorderedSamples do printfn "  REORDERED %s" l
for KeyValue(k, n) in countKinds |> Seq.sortByDescending (fun kv -> kv.Value) do printfn "  count differs, %s: %d names" k n
for l in layoutSamples do printfn "  differs: %s" l
// Fails the oracle: a body that decodes differently from its quotation (the run time would compile
// another body), a signature check, and a reordered layout (a wrong binding the run time cannot
// see). Reported only: a member the encoder cannot carry (the map leaves it out: the attribute is
// needed) and a count that differs (a refusal, the N check).
if differ > 0 || not (exactOk && wrongParam && wrongReturn && lone && otherAssembly) || layoutReordered > 0 || layoutFailed > 0 then exit 1
