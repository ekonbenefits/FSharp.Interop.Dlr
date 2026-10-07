// The repo's analyzer over the generated cases, one file at a time, each file's messages printed
// as it is done (the fsharp-analyzers CLI prints only at the end): the project is type-checked
// once, then each file's check comes from the checker's cache, as in an editor.
//   dotnet fsi analyze.fsx fsc.args   (fsc.args: the project's fsc arguments, one per line)

#r "nuget: FSharp.Analyzers.SDK, 0.39.2"
#r "../FSharp.Interop.Dlr.Analyzers/bin/Release/net10.0/FSharp.Interop.Dlr.Analyzers.dll"

open System.Diagnostics
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Analyzers.SDK
open FSharp.Interop.Dlr.Analyzers

let checker = FSharpChecker.Create(keepAssemblyContents = true)
let args = File.ReadAllLines fsi.CommandLineArgs.[1]
let project = Path.Combine(__SOURCE_DIRECTORY__, "Differential.fsproj")
// The sources are the arguments that are files; the API leaves them among the options.
let isSource (a: string) = not (a.StartsWith "-") && (a.EndsWith ".fs" || a.EndsWith ".fsi")
let options =
    { checker.GetProjectOptionsFromCommandLineArgs(project, args |> Array.filter (isSource >> not)) with
        SourceFiles = args |> Array.filter isSource |> Array.map (fun f -> Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, f))) }
let clock = Stopwatch.StartNew()
let results = checker.ParseAndCheckProject options |> Async.RunSynchronously
eprintfn "checked in %.0fs" clock.Elapsed.TotalSeconds

for file in options.SourceFiles |> Array.filter (fun f -> f.Contains "Cases") do
    clock.Restart()
    let parse, check = checker.GetBackgroundCheckResultsForFileInProject(file, options) |> Async.RunSynchronously
    let source = SourceText.ofString (File.ReadAllText file)
    let context = Utils.createContext results file source (parse, check) (AnalyzerProjectOptions.BackgroundCompilerOptions options)
    let messages = ReflectedDefinitionAnalyzer.cliAnalyzer context |> Async.RunSynchronously
    for m in messages do
        printfn "%s(%d,%d): %A %s : %s" m.Range.FileName m.Range.StartLine m.Range.StartColumn m.Severity m.Code m.Message
    eprintfn "%s: %d messages in %.1fs" (Path.GetFileName file) messages.Length clock.Elapsed.TotalSeconds
