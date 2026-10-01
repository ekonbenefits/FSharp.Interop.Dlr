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
| `DLR005` | Error | a marker out of place inside a block — each a `DlrTranslationException` at the block's first call; the cases are listed below the table |
| `DLR006` | Error | a `dlr { }` in a function or member whose reflected definition FSharp.Core will not decode — it holds `typeof<System.Void>` — so every block in it fails at run time with the not-found error naming the member |

`DLR005` reports: an argument marker — `Dlr.named`, `Dlr.namedOf`, `Dlr.argsOf`, `Dlr.typeArgs`,
`Dlr.typeArgsOf`, `Dlr.out`, `Dlr.ref` — anywhere but as an argument of a call (a member call, `Dlr.invoke`, `Dlr.call` /
`Dlr.apply`, `Dlr.new'`); `Dlr.typeArgs` / `typeArgsOf` not first, or on `Dlr.call` / `Dlr.apply` /
`Dlr.new'` (a value or constructor call takes none); `Dlr.namedOf` or `Dlr.argsOf` twice in one
call; a positional argument after `Dlr.namedOf`; `Dlr.named` on a record in a variable rather
than the literal (names from data are `Dlr.namedOf`); `Dlr.Static<T>.Overloads` anywhere but as
the target of a call; `Dlr.call x` read at a non-function type; a member or value read as a tupled F#
function of more than five elements (curried has no limit); `Dlr.ref` on anything but a
`let mutable`; a call with `Dlr.out` whose result type does not fit (the return value then each
out as a tuple, the outs alone, or the one out's value); `Dlr.out` in `Dlr.new'` (its result is the
`T`; a constructor's `ref` is `Dlr.ref`); `Dlr.out` / `Dlr.ref` in a call with `Dlr.namedOf` /
`Dlr.argsOf`.

## Setup

An FSharp.Analyzers.SDK analyzer runs in the `fsharp-analyzers` tool, and only in the tool of the
exact SDK version it was built against: `fsharp-analyzers` 0.39.2, which needs .NET SDK 10 (8 and
9 are not supported). The build is in `analyzers/dotnet/fs/net10.0`.

The project being analyzed can target anything, `net472` included; only the SDK you build with
matters.

1. The tool, in a manifest so every clone and CI gets the same one:

   ```
   dotnet new tool-manifest            # if there is no .config/dotnet-tools.json yet
   dotnet tool install fsharp-analyzers --version 0.39.2
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
     <FSharpAnalyzersOtherFlags>--analyzers-path "$(PkgFSharp_Interop_Dlr_Analyzers)/analyzers/dotnet/fs/net10.0"</FSharpAnalyzersOtherFlags>
     <!-- Without this, DLR001/DLR002 and a tool that fails to run are all downgraded to warnings
          (MSBuild's Exec with ContinueOnError) and the build succeeds. -->
     <FSharpAnalyzersContinueOnError>false</FSharpAnalyzersContinueOnError>
   </PropertyGroup>
   ```

   The `net10.0` folder, not the package root: the tool looks for analyzers in the directory it
   is given.

3. Check it actually runs: a clean build prints nothing, so remove one `[<ReflectedDefinition>]`
   and build — you should see `error DLR001: dlr { } needs [<ReflectedDefinition>] …`. If not,
   the usual causes are the tool not restored (`dotnet-fsharp-analyzers does not exist`),
   `RunAnalyzers` missing, or `GeneratePathProperty` missing so the path is empty.

Ionide picks the analyzer up from the same path; Rider and Visual Studio do not run SDK
analyzers, so there the check happens at build. `-p:RunAnalyzers=false` skips it.
