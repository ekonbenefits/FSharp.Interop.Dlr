/// Renders each example's compiled tree into the docs between `<!-- tree:name -->` markers (see
/// Trees.fsproj). With `check`, writes nothing and fails if any marker's content is stale.
module Trees.Program

open System
open System.IO
open System.Linq.Expressions
open System.Reflection
open System.Text.RegularExpressions
open AgileObjects.ReadableExpressions
open FSharp.Interop.Dlr

/// The repository root: the nearest ancestor of the binary holding the solution file. Not
/// __SOURCE_DIRECTORY__, which a CI build (ContinuousIntegrationBuild) maps to `/_/`.
let private root =
    let rec up (d: DirectoryInfo) =
        if isNull d then failwith "repository root (FSharp.Interop.Dlr.slnx) not found above the binary"
        elif File.Exists(Path.Combine(d.FullName, "FSharp.Interop.Dlr.slnx")) then d.FullName
        else up d.Parent
    up (DirectoryInfo AppContext.BaseDirectory)
let private pages = [ "docs/trees.md"; "docs/call-sites.md"; "docs/translation.md" ]

/// Each example's source: the lines between `// example: <name>` and `// end` in Examples.fs.
let private sources =
    let lines = File.ReadAllLines(Path.Combine(root, "docs", "trees", "Examples.fs"))
    [ for i, line in Array.indexed lines do
        if line.StartsWith "// example: " then
            let body = lines |> Seq.skip (i + 1) |> Seq.takeWhile (fun l -> l <> "// end") |> String.concat "\n"
            yield line.Substring 12, body ]
    |> Map.ofList

/// The renderer prints a constant as its type; a hoisted site is one, so say so.
let private labelConstants (tree: string) =
    Regex.Replace(tree, @"^(\s*)var (\w+) = (CallSite<.*>);$", "$1var $2 = <constant $3>;", RegexOptions.Multiline)

[<EntryPoint>]
let main args =
#if DEBUG
    eprintfn "Run in Release (-c Release): the docs show the state-machine path, the one users ship."
    exit 2
#endif
    let check = args |> Array.contains "check"
    // The tree of each example, as the library hands it over just before compiling it.
    let trees = Collections.Generic.Dictionary<string, string>()
    let mutable current = ""
    typeof<DlrRun>.Assembly.GetType("FSharp.Interop.Dlr.Translate+TreeHook")
        .GetProperty("Sink", BindingFlags.NonPublic ||| BindingFlags.Public ||| BindingFlags.Static)
        .SetValue(null, Action<Type, LambdaExpression>(fun _ tree -> trees.[current] <- labelConstants (tree.ToReadableString())))
    for name, run in Examples.all do
        current <- name
        run ()

    let block name =
        match Map.tryFind name sources, trees.TryGetValue name with
        | Some source, (true, tree) -> sprintf "```fsharp\n%s\n```\n\n```csharp\n%s\n```" source (tree.TrimEnd())
        | _ -> failwithf "no example named %s in Examples.fs" name
    let marker = Regex(@"(<!-- tree:(\w+) -->).*?(<!-- /tree:\2 -->)", RegexOptions.Singleline)
    let stale =
        [ for page in pages do
            let path = Path.Combine(root, page)
            let text = File.ReadAllText path
            let updated = marker.Replace(text, fun m -> m.Groups.[1].Value + "\n" + block m.Groups.[2].Value + "\n" + m.Groups.[3].Value)
            if updated <> text then
                if not check then File.WriteAllText(path, updated)
                yield page ]
    if check then
        if stale.IsEmpty then printfn "trees: every example in the docs is current"; 0
        else
            for page in stale do eprintfn "trees: %s is stale; run `dotnet run -c Release --project docs/trees`" page
            1
    else
        printfn "trees: %d example(s) rendered; %s" trees.Count (if stale.IsEmpty then "no page changed" else "wrote " + String.Join(", ", stale))
        0
