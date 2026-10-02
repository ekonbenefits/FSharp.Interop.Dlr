module FSharp.Interop.Dlr.Benchmarks.Program

open System
open System.IO
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Reports
open BenchmarkDotNet.Running

let private suites = [| typeof<Core>; typeof<Targets> |]

/// The README's "Measured" rows (method names, in order): the shape of the numbers, not all of them.
/// The README's "Measured" table: two operations, each way, ns only, columns fastest to slowest.
let private readmeColumns = [ "static"; "C# `dynamic`"; "`dlr { }`"; "reflection (cached)"; "FSharp.Interop.Dynamic" ]
let private readmeRows =
    [ "method call `w.Add(i, 1)`", [ "StaticCall"; "CSharpCall"; "Call"; "ReflectionCall"; "DynamicCall" ]
      "property get `w.Count`", [ "StaticGet"; "CSharpGet"; "Get"; "ReflectionGet"; "DynamicGet" ] ]

/// One result per benchmark method: description, mean ns, allocated bytes per call.
let private results (summaries: (Type * Summary) list) =
    [ for (_, summary) in summaries do
        for r in summary.Reports do
            if not (isNull r.ResultStatistics) then
                let case = r.BenchmarkCase
                let allocated = r.GcStats.GetBytesAllocatedPerOperation(case)
                yield case.Descriptor.WorkloadMethod.Name, (case.Descriptor.WorkloadMethodDisplayInfo.Trim('\''), r.ResultStatistics.Mean, (if allocated.HasValue then allocated.Value else 0L)) ]
    |> dict

let private fmtNs (ns: float) =
    // Invariant: the file is compared across machines (compare.fsx), whatever their culture.
    let invariant = Globalization.CultureInfo.InvariantCulture
    if ns >= 1000.0 then String.Format(invariant, "{0:N0}", ns) else String.Format(invariant, "{0:0.#}", ns)

let private cell (results: Collections.Generic.IDictionary<string, string * float * int64>) (name: string) =
    match results.TryGetValue name with
    | true, (_, mean, bytes) -> fmtNs mean + " ns" + (if bytes = 0L then "" else " / " + string bytes + " B")
    | _ -> "—"

/// A comparison: operations down the side, approaches across, each cell one benchmark method
/// (or "" for none). Reads as "the same thing, done N ways".
let private comparison (results: Collections.Generic.IDictionary<string, string * float * int64>) (title: string) (note: string) (approaches: string list) (rows: (string * string list) list) =
    [ yield "### " + title
      yield ""
      if note <> "" then
          yield note
          yield ""
      yield "| | " + String.concat " | " approaches + " |"
      yield "| --- |" + String.concat "" [ for _ in approaches -> " ---: |" ]
      for (label, methods) in rows -> "| " + label + " | " + String.concat " | " [ for m in methods -> if m = "" then "—" elif m.StartsWith "^" then "—[^" + m.Substring 1 + "]" else cell results m ] + " |"
      yield "" ]

/// The dashes, each with its reason, as Markdown footnotes; a cell names its note as `^n`.
let private footnotes =
    [ "1", "FSharp.Interop.Dynamic has no conversion from an F# function to a delegate parameter."
      "2", "the typed API has no single form here: a chain is two casts, an `ExpandoObject` is an `IDictionary`."
      "3", "FSharp.Interop.Dynamic fails on a `JObject` member: its result conversion asks the `JValue` to convert to `object`, which Newtonsoft refuses (\"Can not convert from System.Int64 to System.Object\")."
      "4", "neither C#'s binder nor Dynamitey invokes an F# function value held in a member."
      "5", "an F# optional parameter is an `FSharpOption` with no `[Optional]`: C#'s binder and Dynamitey need it passed."
      "6", "C# reaches a constructor through the binder only with a `dynamic` argument (`new Widget()` is a static call), and FSharp.Interop.Dynamic has no form for it here."
      "7", "C# has no spelling with a run-time member name or argument names, nor for invoking a value read as an F# function."
      "8", "FSharp.Interop.Dynamic takes its arguments as a tuple; a list of unknown length has no form."
      "9", "FSharp.Interop.Dynamic has no spelling for an `out` argument." ]

/// `docs`: run every suite and write docs/benchmarks.md (comparisons) and the README's "Measured"
/// table (`readmeRows`), between its markers.
let private writeDocs (short: bool) =
    // The repo root: the nearest ancestor of the binary holding the solution file.
    let root =
        let rec up (d: DirectoryInfo) =
            if isNull d then failwith "repo root (FSharp.Interop.Dlr.slnx) not found above the binary"
            elif File.Exists(Path.Combine(d.FullName, "FSharp.Interop.Dlr.slnx")) then d.FullName
            else up d.Parent
        up (DirectoryInfo AppContext.BaseDirectory)
    let config =
        let c = ManualConfig.Create(DefaultConfig.Instance).WithOptions(ConfigOptions.DisableOptimizationsValidator)
        if short then c.AddJob(Job.ShortRun.AsDefault()) else c
    let summaries = [ for t in suites -> t, BenchmarkRunner.Run(t, config) ]
    let r = results summaries
    let env = summaries.Head |> snd |> fun s -> s.HostEnvironmentInfo.ToFormattedString() |> Seq.truncate 5 |> String.concat "  \n"
    let full =
        [ yield "# Benchmarks"
          yield ""
          yield "Generated by `Benchmarks/bench.sh docs` (BenchmarkDotNet, " + (if short then "short job" else "default job") + "). Every `dlr { }` is bound and"
          yield "compiled during warm-up, so a cell is the steady-state cost of one call and what it allocates."
          yield "The same operation is done each way it can be — statically, as C# `dynamic` (the same"
          yield "Microsoft.CSharp binders, the compiler's own call sites), in a `dlr { }`, through cached"
          yield "reflection, and with FSharp.Interop.Dynamic 6.0 — the columns ordered roughly fastest to"
          yield "slowest. A dash is a cell with nothing to time, footnoted with why (or, under \"Where the forms"
          yield "differ\", explained by that table's note)."
          yield ""
          yield env
          yield ""
          yield "## The same operation, each way"
          yield ""
          yield! comparison r "Members" "" [ "static"; "C# `dynamic`"; "`dlr { }`"; "reflection (cached)"; "FSharp.Interop.Dynamic" ]
                   [ "property get `w.Count`", [ "StaticGet"; "CSharpGet"; "Get"; "ReflectionGet"; "DynamicGet" ]
                     "method call `w.Add(i, 1)`", [ "StaticCall"; "CSharpCall"; "Call"; "ReflectionCall"; "DynamicCall" ]
                     "property set `w.Name <- v`", [ "StaticSet"; "CSharpSet"; "Set"; "ReflectionSet"; "DynamicSet" ]
                     "100 method calls in one loop — the whole loop, so ÷100 per call", [ "StaticLoop"; "CSharpLoop"; "Loop"; "ReflectionLoop"; "DynamicLoop" ] ]
          yield! comparison r "Operators, indexers, delegates, conversions" "" [ "C# `dynamic`"; "`dlr { }`"; "FSharp.Interop.Dynamic" ]
                   [ "`a + b` on boxed ints", [ "CSharpAdd"; "Add"; "DynamicAdd" ]
                     "indexer `d[\"a\"]` on a dictionary", [ "CSharpIndex"; "Index"; "DynamicIndex" ]
                     "invoke a delegate value with 20", [ "CSharpInvokeDelegate"; "InvokeDelegate"; "DynamicInvokeDelegate" ]
                     "implicit conversion of a boxed int to int64", [ "CSharpConvert"; "Convert"; "DynamicConvert" ]
                     "static method chosen by an argument's runtime type", [ "CSharpStaticOverloads"; "StaticOverloads"; "DynamicStatic" ]
                     "a lambda for a `Func` parameter (C#: a `Func` literal; dlr: an F# lambda in the block)", [ "CSharpRunFunc"; "FunctionToDelegate"; "^1" ]
                     "named arguments `d.Add(b: 1, a: i)`", [ "CSharpNamedArgs"; "NamedArgs"; "DynamicNamedArgs" ]
                     "an `out` argument, `d.TryGetValue(k, out v)` with the result used (dlr: `Dlr.out`, a tuple)", [ "CSharpOutArg"; "OutArg"; "^9" ]
                     "the same into a struct tuple (dlr: `let struct (found, v) = …`)", [ "CSharpOutArg"; "OutArgStruct"; "^9" ] ]
          yield! comparison r "Where the forms differ"
                   "Each column does what its language offers here, so the cells are not always like for like: `==` on records is reference equality for C# `dynamic` and FSharp.Interop.Dynamic, structural for `dlr { }`; FSharp.Interop.Dynamic's `!?f` invokes a value where `dlr` reads it as a function first; a dash is a form that language has no spelling for, footnoted."
                   [ "C# `dynamic`"; "`dlr { }`"; "FSharp.Interop.Dynamic" ]
                   [ "F# function member `w?Fn(1, 2)`", [ "^4"; "FunctionMember"; "^4" ]
                     "optional parameter omitted `w?Bump(1)`", [ "^5"; "OptionalOmitted"; "^5" ]
                     "record `==` (structural only for `dlr`)", [ "CSharpEquals"; "StructuralEquals"; "DynamicEquals" ]
                     "constructor through the binder, `Dlr.new'<Widget>()`", [ "^6"; "Construct"; "^6" ]
                     "member name from a variable, alternating between two", [ "^7"; "ComputedName"; "DynamicComputedName" ]
                     "keyword arguments from data (`Dlr.namedOf kwargs`; `Dyn.namedArg` pairs)", [ "^7"; "NamedOf"; "DynamicNamedOf" ]
                     "positional arguments from data, `Dlr.argsOf args`", [ "^7"; "ArgsOf"; "^8" ]
                     "keyword arguments from data, two name lists alternating", [ "^7"; "NamedOfAlternating"; "DynamicNamedOfAlternating" ]
                     "a six-argument call `w?Sum6(1, …, 6)`", [ "CSharpCallSum6"; "CallSum6"; "" ]
                     "a member read as a tupled function of five, then applied (`w?Sum5` typed `int * … -> int`; a typed helper)", [ "^7"; "TupledRead5"; "" ]
                     "the same of six (compiled once per site, one step over the tuple)", [ "^7"; "TupledRead6"; "" ]
                     "a member read as a curried function of six, then applied (compiled once per site, a step per argument)", [ "^7"; "CurriedRead6"; "" ]
                     "a value read as an F# function, `Dlr.call f : int -> int -> int`, then applied (`!?f (1, 2)`, which takes a tupled function, not a curried one)", [ "^7"; "CallAsFunction"; "DynamicInvokeFunction" ] ]
          yield "## Real targets"
          yield ""
          yield! comparison r "Newtonsoft.Json `JObject` and `ExpandoObject`" "" [ "typed API"; "C# `dynamic`"; "`dlr { }`"; "FSharp.Interop.Dynamic" ]
                   [ "`JObject` `j.count`", [ "JObjectStatic"; "CSharpJObjectGet"; "JObjectGet"; "^3" ]
                     "`JObject` `j.owner.name`", [ "^2"; "CSharpJObjectChain"; "JObjectChain"; "^3" ]
                     "`ExpandoObject` `e.count`", [ "^2"; "CSharpExpandoGet"; "ExpandoGet"; "DynamicExpandoGet" ] ]
          yield "Allocation per call is the box for a value-typed result — the same box C# `dynamic` pays — and"
          yield "nothing for the block itself (a struct state machine) — see `Tests/HotPath.fs`. `Benchmarks/README.md` explains the suites."
          yield ""
          for (n, text) in footnotes -> "[^" + n + "]: " + text ]
        |> String.concat "\n"
    File.WriteAllText(Path.Combine(root, "docs", "benchmarks.md"), full + "\n")
    let readmePath = Path.Combine(root, "README.md")
    let readme = File.ReadAllText readmePath
    let startMarker, endMarker = "<!-- benchmarks:start -->", "<!-- benchmarks:end -->"
    let i, j = readme.IndexOf startMarker, readme.IndexOf endMarker
    if i < 0 || j < 0 then eprintfn "README.md has no %s / %s markers; table not written" startMarker endMarker
    else
        let ns (name: string) = match r.TryGetValue name with | true, (_, mean, _) -> fmtNs mean | _ -> "—"
        // The `dlr { }` column in bold: it is the one the reader came for.
        let dlrColumn = readmeColumns |> List.findIndex (fun c -> c = "`dlr { }`")
        let cells (methods: string list) = methods |> List.mapi (fun i m -> if i = dlrColumn then "**" + ns m + "**" else ns m)
        let table =
            [ yield "| ns per call | " + String.concat " | " (readmeColumns |> List.mapi (fun i c -> if i = dlrColumn then "**" + c + "**" else c)) + " |"
              yield "| --- |" + String.concat "" [ for _ in readmeColumns -> " ---: |" ]
              for (label, methods) in readmeRows -> "| " + label + " | " + String.concat " | " (cells methods) + " |" ]
            |> String.concat "\n"
        let replaced = readme.Substring(0, i + startMarker.Length) + "\n" + table + "\n" + readme.Substring j
        File.WriteAllText(readmePath, replaced)
        printfn "Wrote docs/benchmarks.md and the README table (%d rows)." readmeRows.Length

[<EntryPoint>]
let main args =
    match List.ofArray args with
    | "docs" :: rest -> writeDocs (List.contains "short" rest); 0
    | _ ->
        // `dotnet run -c Release -- --filter '*Core*'`, `-- --job short`, or no arguments for the menu.
        BenchmarkSwitcher.FromTypes(suites).Run(args) |> ignore
        0
