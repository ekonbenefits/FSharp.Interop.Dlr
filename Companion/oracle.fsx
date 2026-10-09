// The body pass's oracle (#219): encodes each member that holds a dlr { } block as the build
// companion does (FSharp.Interop.Dlr.Build/Bodies.fs), decodes it as the run time does (BodyMap.fs),
// through the map's text, and compares the result with the member's [<ReflectedDefinition>]
// quotation, modulo variable identity. Every member with a reflected definition should be the same.
//
//   dotnet fsi oracle.fsx <fsc args file> <project dir> <built assembly>

#r "nuget: FSharp.Compiler.Service, 43.12.400"
#load "../FSharp.Interop.Dlr/BodyMap.fs" "../FSharp.Interop.Dlr.Build/Check.fs" "../FSharp.Interop.Dlr.Build/Bodies.fs"

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
let candidates = members results.AssemblyContents

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
