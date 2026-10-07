// Release against Debug, per case: the same results, or Release refusing the block with a
// DlrTranslationException. Anything else is a block bound silently wrong, and fails (exit 1).
// A case Debug itself fails is listed too: Debug is the oracle. Given the analyzer's output too,
// its DLR007 warnings must be the cases Release refuses as two variables of one name, no more
// and no fewer.
//   dotnet fsi compare.fsx debug.txt release.txt [analyzer.txt]

open System.IO

let read path =
    File.ReadAllLines path
    |> Array.map (fun l -> l.Split '\t')
    |> Array.map (fun a -> a.[0], a)
    |> Map.ofArray

let args = fsi.CommandLineArgs |> Array.skip 1
let debug, release = read args.[0], read args.[1]
let isRefusal (s: string) = s.StartsWith "DlrTranslationException"
let isError (s: string) = s.Contains "Exception: "

let mutable same, refused, wrong, debugFails = 0, 0, 0, 0
for KeyValue(id, d) in debug do
    let r = release.[id]
    if isError d.[1] || isError d.[2] then
        debugFails <- debugFails + 1
        printfn "DEBUG FAILS %s: %s" id d.[1]
    elif r.[1] = d.[1] && r.[2] = d.[2] then same <- same + 1
    elif isRefusal r.[1] && isRefusal r.[2] then refused <- refused + 1
    else
        wrong <- wrong + 1
        printfn "WRONG %s: Debug %s / %s, Release %s / %s (fields: %s)" id d.[1] d.[2] r.[1] r.[2] r.[3]

printfn "%d cases: %d the same, %d refused in Release, %d wrong, %d failing in Debug" debug.Count same refused wrong debugFails

/// The case each DLR007 warning is in: the generated member around its line.
let mutable analyzerMismatches = 0
if args.Length > 2 then
    let warning = System.Text.RegularExpressions.Regex @"(Cases\d+\.g\.fs)\((\d+),\d+\): Warning DLR007"
    let caseAt =
        let dir = Path.Combine(__SOURCE_DIRECTORY__, "Cases")
        let byFile =
            Directory.GetFiles(dir, "*.g.fs")
            |> Array.map (fun f -> Path.GetFileName f, File.ReadAllLines f)
            |> dict
        fun file (line: int) ->
            let lines = byFile.[file]
            seq { line - 1 .. -1 .. 0 }
            |> Seq.pick (fun i -> if lines.[i].StartsWith "let " then Some(lines.[i].Split(' ').[1]) else None)
    let warned =
        File.ReadAllLines args.[2]
        |> Array.choose (fun l -> let m = warning.Match l in if m.Success then Some(caseAt m.Groups.[1].Value (int m.Groups.[2].Value)) else None)
        |> Set.ofArray
    // DLR007 is the two-variables refusal only; a tuple Release cannot rebuild is refused otherwise.
    let refusedIds = release |> Map.filter (fun _ r -> isRefusal r.[1] && r.[1].Contains "reaches two variables") |> Map.keys |> Set.ofSeq
    for id in Set.difference refusedIds warned do
        analyzerMismatches <- analyzerMismatches + 1
        printfn "NOT WARNED %s: Release refuses it, DLR007 is silent" id
    for id in Set.difference warned refusedIds do
        analyzerMismatches <- analyzerMismatches + 1
        printfn "WARNED %s: DLR007 warns, Release runs it" id
    printfn "DLR007: %d warned, %d refused, %d differ" warned.Count refusedIds.Count analyzerMismatches

exit (if wrong > 0 || debugFails > 0 || analyzerMismatches > 0 then 1 else 0)
