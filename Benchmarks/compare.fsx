// Flags benchmark regressions: compares docs/benchmarks.md as regenerated (`./bench.sh docs`)
// with the committed one.
//   dotnet fsi compare.fsx                    the working copy against HEAD
//   dotnet fsi compare.fsx master             ... against another ref
//   dotnet fsi compare.fsx old.md new.md      two files
// Only the `dlr { }` column is judged: a cell regresses when it is over 25% and 2 ns slower, or
// allocates more (bytes are deterministic, so any increase counts). The other columns are not ours;
// their median ratio is printed as the machine's drift, to read a slower `dlr` column against.
// Exit code 1 when anything regressed.
open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

let timeLimit, timeFloor = 1.25, 2.0

let git (args: string) =
    let psi = ProcessStartInfo("git", args, RedirectStandardOutput = true, UseShellExecute = false)
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    if p.ExitCode <> 0 then failwithf "git %s failed" args
    out

let repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let docs = Path.Combine(repo, "docs", "benchmarks.md")
let oldText, newText =
    match fsi.CommandLineArgs |> Array.tail with
    | [| a; b |] -> File.ReadAllText a, File.ReadAllText b
    | [| ref' |] -> git (sprintf "-C \"%s\" show %s:docs/benchmarks.md" repo ref'), File.ReadAllText docs
    | _ -> git (sprintf "-C \"%s\" show HEAD:docs/benchmarks.md" repo), File.ReadAllText docs

/// A cell's time (ns) and bytes, or None for a dash.
let parse (cell: string) =
    let m = Regex.Match(cell, @"^([\d,.]+) ns(?: / (\d+) B)?")
    if not m.Success then None
    else Some(float (m.Groups.[1].Value.Replace(",", "")), (if m.Groups.[2].Success then int64 m.Groups.[2].Value else 0L))

/// Every timed cell, keyed by (section, row, column).
let cells (text: string) =
    let mutable section = ""
    let mutable header = [||]
    [ for line in text.Split('\n') do
        let line = line.TrimEnd()
        if line.StartsWith "#" then section <- line.TrimStart('#', ' ')
        elif line.StartsWith "|" then
            let cols = line.Trim('|').Split('|') |> Array.map (fun c -> c.Trim())
            if cols.[0] = "" then header <- cols
            elif not (cols.[0].StartsWith "---") && header.Length = cols.Length then
                for i in 1 .. cols.Length - 1 do
                    match parse cols.[i] with
                    | Some v -> yield (section, cols.[0], header.[i]), v
                    | None -> () ]
    |> Map.ofList

let before, after = cells oldText, cells newText
let isDlr (_, _, column: string) = column.Contains "dlr"
let common = [ for KeyValue(k, v) in after do match before.TryFind k with Some o -> yield k, o, v | None -> () ]

let ratios = [ for k, (t0, _), (t1, _) in common do if not (isDlr k) && t0 > 0.0 then yield t1 / t0 ] |> List.sort
if not ratios.IsEmpty then printfn "machine drift (median of the other columns): %+.0f%%" ((ratios.[ratios.Length / 2] - 1.0) * 100.0)

let regressions =
    [ for (section, row, column as k), (t0, b0), (t1, b1) in common do
        if isDlr k then
            let slower = t1 > t0 * timeLimit && t1 - t0 > timeFloor
            if slower || b1 > b0 then
                yield sprintf "  %s / %s [%s]: %g ns / %d B -> %g ns / %d B" section row column t0 b0 t1 b1 ]
let improved = [ for k, (t0, b0), (t1, b1) in common do if isDlr k && (t1 < t0 / timeLimit || b1 < b0) then yield k ]
let onlyIn (a: Map<_, _>) (b: Map<_, _>) = [ for KeyValue(k, _) in a do if isDlr k && not (b.ContainsKey k) then yield k ]

printfn "%d dlr cells compared, %d improved" (common |> List.filter (fun (k, _, _) -> isDlr k) |> List.length) improved.Length
for section, row, _ in onlyIn after before do printfn "new (no baseline): %s / %s" section row
for section, row, _ in onlyIn before after do printfn "gone or renamed: %s / %s" section row
if regressions.IsEmpty then printfn "no regressions"
else
    printfn "%d regression(s):" regressions.Length
    regressions |> List.iter (printfn "%s")
    exit 1
