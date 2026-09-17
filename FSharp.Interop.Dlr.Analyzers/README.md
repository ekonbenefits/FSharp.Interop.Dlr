# FSharp.Interop.Dlr.Analyzers

Build-time check for [FSharp.Interop.Dlr](https://github.com/ekonbenefits/FSharp.Interop.Dlr):
a `dlr { }` block needs `[<ReflectedDefinition>]` on the function or member that contains it
(or on an enclosing module or type), and without it the first call raises. This analyzer reports
that at build time, and in Ionide, with a fix that adds the attribute to the outermost binding
containing the block, i.e. the function or member the compiler stores a definition for (a local
function inside it is a closure and cannot carry the attribute). A block in module-level `do`
code is reported without a fix: move it into a function.

| Code | Severity | Reports |
| --- | --- | --- |
| `DLR001` | Error | a `dlr { }` with no `[<ReflectedDefinition>]` on its enclosing function/member, module or type |

Wire it up the way any FSharp.Analyzers.SDK analyzer is:

```xml
<PackageReference Include="FSharp.Analyzers.Build" Version="0.5.0" PrivateAssets="all" />
<PackageReference Include="FSharp.Interop.Dlr.Analyzers" Version="*" GeneratePathProperty="true" PrivateAssets="all" />
```

```xml
<PropertyGroup>
  <FSharpAnalyzersOtherFlags>--analyzers-path "$(PkgFSharp_Interop_Dlr_Analyzers)/analyzers/dotnet/fs"</FSharpAnalyzersOtherFlags>
</PropertyGroup>
```

Ionide picks it up from the same path; Rider and Visual Studio do not run SDK analyzers, so
there the check happens at build.
