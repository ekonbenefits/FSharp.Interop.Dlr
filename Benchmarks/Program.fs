module FSharp.Interop.Dlr.Benchmarks.Program

open BenchmarkDotNet.Running

[<EntryPoint>]
let main args =
    // `dotnet run -c Release -- --filter '*Core*'`, `-- --job short`, or no arguments for the menu.
    BenchmarkSwitcher.FromAssembly(typeof<Core>.Assembly).Run(args) |> ignore
    0
