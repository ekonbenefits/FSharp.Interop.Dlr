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
| `DLR003` | Error | two or more `dlr { }` blocks starting on one source line (a block is found by the line of its `Run` call; the first call raises `DlrTranslationException`) |
| `DLR004` | Error | a `dlr { }` inside an `inline` function or member (declaration-level or a local `let inline`): in Release the function is expanded into every caller, where the block's captured values are inlined away and its body is not where the reflected definition says — a Debug build calls it as a method, so it only appears to work there; remove `inline` or move the block out. A block DLR004 refuses gets no DLR001 |
| `DLR005` | Error | an argument marker — `Dlr.named`, `Dlr.namedOf`, `Dlr.typeArgs`, `Dlr.typeArgsOf` — anywhere but as an argument of a call (a member call, `Dlr.invoke`, `Dlr.call` / `Dlr.apply`, `Dlr.new'`), `Dlr.typeArgs`/`typeArgsOf` not first, `Dlr.typeArgs`/`typeArgsOf` on `Dlr.call` / `Dlr.apply` / `Dlr.new'` (a value or constructor call takes none), or `Dlr.namedOf` twice in one call; each raises `DlrTranslationException` at the block's first call |

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
     <!-- The build for your tool: net10.0 on .NET SDK 10, net8.0 on SDK 8/9. Not the package root. -->
     <DlrAnalyzerFolder>net10.0</DlrAnalyzerFolder>
     <DlrAnalyzerFolder Condition="$([MSBuild]::VersionLessThan('$(NETCoreSdkVersion)', '10.0'))">net8.0</DlrAnalyzerFolder>
     <FSharpAnalyzersOtherFlags>--analyzers-path "$(PkgFSharp_Interop_Dlr_Analyzers)/analyzers/dotnet/fs/$(DlrAnalyzerFolder)"</FSharpAnalyzersOtherFlags>
     <!-- Without this, DLR001/DLR002 and a tool that fails to run are all downgraded to warnings
          (MSBuild's Exec with ContinueOnError) and the build succeeds. -->
     <FSharpAnalyzersContinueOnError>false</FSharpAnalyzersContinueOnError>
   </PropertyGroup>
   ```

   Not the package root: it holds both builds, and while tool 0.39.2 just logs that it skipped
   the other one, tool 0.36.0 reports the diagnostics and then exits with -3 ("failed to load
   some assemblies"), which is a build failure with `FSharpAnalyzersContinueOnError=false`.

3. Check it actually runs: a clean build prints nothing, so remove one `[<ReflectedDefinition>]`
   and build — you should see `error DLR001: dlr { } needs [<ReflectedDefinition>] …`. If not,
   the usual causes are the tool not restored (`dotnet-fsharp-analyzers does not exist`),
   `RunAnalyzers` missing, or `GeneratePathProperty` missing so the path is empty.

Ionide picks the analyzer up from the same path; Rider and Visual Studio do not run SDK
analyzers, so there the check happens at build. `-p:RunAnalyzers=false` skips it.
