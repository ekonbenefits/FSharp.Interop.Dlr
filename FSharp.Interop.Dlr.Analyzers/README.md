# FSharp.Interop.Dlr.Analyzers

Build-time checks for [FSharp.Interop.Dlr](https://github.com/ekonbenefits/FSharp.Interop.Dlr).
A `dlr { }` block needs `[<ReflectedDefinition>]` on the function or member that contains it
(or on an enclosing module or type), and without it the first call raises; this analyzer reports
that at build time, and in Ionide, with a fix that adds the attribute to the outermost binding
containing the block, i.e. the function or member the compiler stores a definition for (a local
function inside it is a closure and cannot carry the attribute). A block in module-level `do`
code is reported without a fix: move it into a function.

| Code | Severity | Reports |
| --- | --- | --- |
| `DLR001` | Error | a `dlr { }` with no `[<ReflectedDefinition>]` on its enclosing function/member, module or type |
| `DLR002` | Error | a `?` operator or `Dlr.*` marker used outside any `dlr { }` (it is only ever quoted; executed, it throws `InvalidOperationException`) |

## Setup

An FSharp.Analyzers.SDK analyzer runs in the `fsharp-analyzers` tool, and only in the tool of the
exact SDK version it was built against, so the package carries two builds:

| Your .NET SDK | Tool to install | Analyzer folder in the package |
| --- | --- | --- |
| 10 | `fsharp-analyzers` 0.39.2 | `analyzers/dotnet/fs/net10.0` |
| 8 or 9 | `fsharp-analyzers` 0.36.0 (the last that installs there) | `analyzers/dotnet/fs/net8.0` |

The project being analyzed can target anything, `net472` included; only the SDK you build with
matters.

1. The tool, in a manifest so every clone and CI gets the same one:

   ```
   dotnet new tool-manifest            # if there is no .config/dotnet-tools.json yet
   dotnet tool install fsharp-analyzers --version 0.39.2   # or 0.36.0 on SDK 8/9
   ```

   (`dotnet tool restore` on a fresh clone.)

2. In the project:

   ```xml
   <PackageReference Include="FSharp.Analyzers.Build" Version="0.5.0" PrivateAssets="all" />
   <PackageReference Include="FSharp.Interop.Dlr.Analyzers" Version="*" GeneratePathProperty="true" PrivateAssets="all" />
   ```

   ```xml
   <PropertyGroup>
     <!-- F# projects do not set this by default; without it the analyzer never runs. -->
     <RunAnalyzers>true</RunAnalyzers>
     <FSharpAnalyzersOtherFlags>--analyzers-path "$(PkgFSharp_Interop_Dlr_Analyzers)/analyzers/dotnet/fs"</FSharpAnalyzersOtherFlags>
     <!-- Optional: DLR001/DLR002 fail the build instead of warning. -->
     <FSharpAnalyzersContinueOnError>false</FSharpAnalyzersContinueOnError>
   </PropertyGroup>
   ```

   The package root holds both builds and the tool skips the one made for the other SDK
   version (logging that it did); to keep the log clean, point at the `net10.0` or `net8.0`
   subfolder for your SDK instead.

3. Check it actually runs: a clean build prints nothing, so remove one `[<ReflectedDefinition>]`
   and build — you should see `error DLR001: dlr { } needs [<ReflectedDefinition>] …`. If not,
   the usual causes are the tool not restored (`dotnet-fsharp-analyzers does not exist`),
   `RunAnalyzers` missing, or `GeneratePathProperty` missing so the path is empty.

Ionide picks the analyzer up from the same path; Rider and Visual Studio do not run SDK
analyzers, so there the check happens at build. `-p:RunAnalyzers=false` skips it.
