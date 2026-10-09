namespace FSharp.Interop.Dlr.Build

open System.IO
open FSharp.Compiler.CodeAnalysis

/// The one FCS check every pass reads: the project's own fsc arguments (MSBuild's
/// `FscCommandLineArgs`, one per line), checked with the assembly contents kept.
module Check =

    let project (argsFile: string) (projectDir: string) : FSharpCheckProjectResults =
        let args = File.ReadAllLines argsFile
        let isSource (a: string) = not (a.StartsWith "-") && (a.EndsWith ".fs" || a.EndsWith ".fsi")
        let checker = FSharpChecker.Create(keepAssemblyContents = true)
        let options =
            { checker.GetProjectOptionsFromCommandLineArgs(Path.Combine(projectDir, "project.fsproj"), args |> Array.filter (isSource >> not)) with
                SourceFiles = args |> Array.filter isSource |> Array.map (fun f -> Path.GetFullPath(Path.Combine(projectDir, f))) }
        let results = checker.ParseAndCheckProject options |> Async.RunSynchronously
        let errors = results.Diagnostics |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
        if errors.Length > 0 then failwithf "the project does not check: %A" (Array.truncate 3 errors)
        results
