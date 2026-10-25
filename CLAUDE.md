# FSharp.Interop.Dlr

`dlr { }` = C# `dynamic` for F#: markers (`?`, `Dlr.*`, `Dlr.Static<T>.Overloads`) are read from
the block's `[<ReflectedDefinition>]` body and compiled once per site into Microsoft.CSharp call
sites. Design: `docs/internals.md` (index; pipeline, caches, call-sites, binders, translation pages). Skills: `check-review`, `new-binder`.

## Gate before pushing

```
dotnet build Tests/Tests.fsproj -c Debug -f net10.0 --no-incremental   # runs the analyzer over the tests
dotnet test -c Debug
dotnet test -c Release            # Release inlining has broken things Debug passed
dotnet test -c Release -p:FSharpCore=latest   # the suite on the newest FSharp.Core (default: the floor)
dotnet build Tests/Tests.fsproj -c Release -f net10.0 -p:DlrClosures=true && dotnet Tests/bin/Release/net10.0/Tests.dll   # every block on the closure path (Tests.ClosureBuilder)
dotnet pack FSharp.Interop.Dlr -c Release -o /tmp/dlr-pkgs   # package validation: binary compatibility with the last release
dotnet build Tests.Wasm -c Release && (cd Tests.Wasm/bin/Release/net10.0-browser/wwwroot && bun runtests.mjs)
dotnet fsi docs/check-identifiers.fsx   # every identifier and path the docs name still exists
FSharp.Interop.Dlr/check-internals.sh  # each idiom fixed more than once has one home (#163)
```

- The first line matters: the analyzer step runs only when `fsc` actually recompiles, so an
  incremental `dotnet test` can show green with the analyzer never having run (a prefix bug in
  it once shipped that way). `--no-incremental` forces it; `error DLR001`/`DLR002` fails the build.
- Analyzer tests (`Analyzers.Tests`) run as part of `dotnet test`; run their dll directly to see
  them alone. CI treats warnings as errors.
- The closure-path line rebuilds `Tests` in place, so rebuild normally before the other lines
  (Debug too when the closure path changed). The pack line validates only when the package is
  rebuilt: an unchanged build skips it as up to date, so clean first to re-check a suppression edit.
- CI (`build.yml`) runs on every PR and every push to master (which publishes a prerelease,
  refreshes the badges on the `badges` branch, and deploys the test and coverage HTML reports to
  GitHub Pages, https://ekonbenefits.github.io/FSharp.Interop.Dlr/, which the badges link to); the repo is public, so Actions minutes are free. net48 is its own job
  (`net48`, Windows), so a .NET Framework failure does not read as the Windows net10.0 leg's;
  it also collects coverage, which `coverage` merges with net10's Debug and Release (the
  .NET Framework-only paths are covered nowhere else).
  `dotnet11` builds and runs the suite on the .NET 11 preview SDK (compiler, runtime, analyzer
  host) as a non-blocking early warning (#67): a red `dotnet11` job is a heads-up to look at, not
  a merge blocker (`report` runs regardless); fold it into `test` when 11 ships.
  `compilers` builds the `Tests` project alone with the 8.0 and 9.0 SDKs for their own
  frameworks (they cannot target net10.0 or read the `.slnx`), normally and through the closure
  builder (#253). Not support for 8/9: a canary that the library relies only on compiler behaviour
  that has held across versions (resumable code, quotations, closure layout); it does
  not gate publishing. A red 8.0 leg may be #254 (a deep block's conversion on a small default
  thread stack), not the compiler. `test` and `coverage` also run the suite through the closure
  builder, so the closure path stays tested and in the coverage report whatever Debug builds.
  `pack` validates the package (below).
  `zizmor` audits the workflows (a finding fails it): actions are pinned by SHA with a
  `# vX.Y.Z` comment, which Dependabot bumps weekly; the token is `contents: read` unless a
  job that writes widens its own `permissions:`; checkouts set `persist-credentials: false`. Run `zizmor .github/` locally before pushing a
  workflow change.
  The local gate still comes first: CI has no macOS leg, and a red push costs a cycle.
- Captures (`Captures`, `letDefinition`, the analyzer's walk) were checked against a generated
  Debug-vs-Release differential test (#200), kept outside the repository; a change there is worth
  rerunning it, and every shape it found is pinned in `Tests/Cache.fs`.
- A cold review by a general-purpose subagent (read the diff, verify every claim with a test,
  no edits) has caught things Copilot missed; worth one per non-trivial PR when asked.

## Conventions

- The builder is resumable code (`inline` members, `ResumableCode<'D, 'T>` types; `Run` is
  `__stateMachine` in `task`'s shape): in Release each block is a struct state machine and its
  type keys `Machines<'SM, 'T>`; in Debug on the 10.0 compiler `__useResumableCode` is false
  and `DlrRun.Closure` unwraps the Delay closure (field `delayed` of the delegate's target) into
  the closure path, while the 8.0, 9.0 and 11.0 compilers build machines in Debug too
  (`DEBUG_MACHINES`, defined in `Directory.Build.targets`; #235, #253). Both paths must stay
  correct, so the closure path is tested on purpose, not by which compiler builds Debug: see
  `Tests.ClosureBuilder` below. Every `dlr { }` compiles statically in
  Release except one shape the compiler declines silently (no FS3511): a function-typed result
  applied on the spot, `(dlr { … } : unit -> R) ()` — which the optimizer also makes of a
  function-typed block bound with `let` and applied once, fully or partially (it inlines the binding; #217,
  `Tests/Resumable.fs`) — and which takes the closure path, with a
  null delegate target when nothing is captured (`DlrRun.Closure` compiles from the closure
  class). FS3511 itself arises only from the builder's members called by hand with the `Delay`
  result bound or passed separately, which nobody writes.
- Tests: AnyUnit xunit style, `[<Fact>]` + FsUnit `should`; test modules are
  `[<ReflectedDefinition>]`; `Tests.Wasm` links the same files, so add new test files to both
  fsproj files. Tests needing real threads skip with `AnyUnit.IgnoreException` when
  `ProcessorCount < 2` (wasm) rather than passing vacuously. Two styles: *usage* tests read
  like real code — `let w: obj = Widget()`, a named typed `let` per result with the assertion
  on its own line, or a function taking the target (`Members`, `Invoke`, the real-target files);
  *boundary* tests are one case per line, `(dlr { … } : T) |> should equal v` (`Restrictions`,
  `Delegates`, `Operators`). A fixture no quotation can hold (a `byref` member) goes in a
  non-reflected type (`Unquotable`); a member whose stored quotation FSharp.Core cannot decode
  (`typeof<System.Void>`, DLR006) stays out of `Tests.Wasm` entirely — Mono asserts and the
  whole process dies (`Tests/Undecodable.fs`).
- Translator forms that hoist a binding ahead of the site call (a `Dlr.named` record's
  temporaries, a splat list, a tuple, a computed key) go through `sequenced` in `TranslatePatterns.fs`,
  or C#'s order (target, then arguments left to right) breaks silently; the order test in
  `Tests/Invoke.fs` covers only the forms that exist. A cache keyed by a run-time value
  validates the key on the miss path only (`SiteCache.Get`, `NamedOfCache.Get`): the hit path
  stays allocation-free.
- Analyzer: `Analyzers.Tests` type-checks its sources against a stand-in copy of the `Dlr`
  API in its prelude, so a new `Dlr` member goes there too. Typed-tree shapes to know: an
  over-applied call's `Call` node `.Type` is not its function type (tell an application
  structurally); a multi-field anonymous record literal arrives let-bound; `Dlr.new'`
  arguments arrive `Coerce`d to `obj`; a piped partial application is an eta-expanded lambda
  chain (`head`/`describe` in `ReflectedDefinitionAnalyzer.fs` descend those).
- Translation errors: a `DlrTranslationException` is a shape the translator rejects, and that
  shape is visible in the typed tree, so every new one gets an analyzer check (DLR005 for a
  marker out of place, or a new code) unless the analyzer genuinely cannot see it — say why
  in the PR if not. The test that pins the run-time error carries a `fsharpanalyzer: ignore`
  comment saying the analyzer reports it at build time (`Tests/StaticOverloads.fs` is the shape).
- Markers: `[<MethodImpl(NoInlining)>]`, throw outside a block, and every one is in the
  `Errors.fs` outside-a-block test. New `Dlr` members are analyzer markers automatically; types
  in the `Dlr` module (`DlrModule` to FCS) too — never widen the analyzer's prefix past
  `FSharp.Interop.Dlr.DlrModule.` (`DlrCache`, `DlrRuntime`, `DlrBuilder` share the prefix).
- No lambda-form API; `Dlr.cast` is conversion only (explicit interfaces: static cast); no
  static property/field/event access — `Dlr.Static<T>.Overloads` is for calls only, a static
  property is `T.P` in plain F#.
- A user's delegate type's members go through `DelegateMembers.invokeOf`/`constructorOf`, never
  `GetMethod("Invoke")`/`GetConstructor`: F# compiles an `internal` delegate's `Invoke` and
  constructor internal, and the public lookup returns null (#121 fixed the literal paths, #150 the
  conversions it missed). A plain lookup is fine only on always-public types: `Func`/`Action`,
  `FSharpFunc`, the generated adapters, site and factory delegates. Tests: the `Internal*`
  delegate fixtures (`Tests/Fixtures.fs`) give each delegate shape an internal variant.
- The F# binder rules are the *seam* (`docs/binders.md`): where C# fails or binds against F#'s
  expectation on what F# hands it. A rule goes before C#'s only where C# would bind *wrongly*
  or crash rather than fail (structural `==`, a `Delegate`-typed slot, invoking an `internal`
  delegate on .NET Framework); otherwise ours is C#'s error suggestion. See `new-binder`.
- wasm: a nested non-capturing lambda loses its arguments on Mono's interpreter, and
  `FuncConvert` wrappers do too; `capturing` in `TranslateBlock.fs` and the typed wrappers in
  `Functions.fs` exist for that — do not "simplify" them away. A delegate type emitted at run
  time (a site or per-key delegate past `Func`'s 17 type parameters, i.e. 15 arguments and up) must never be
  named in a quotation: FSharp.Core's checks call `Assembly.ReflectionOnly` on it, unimplemented
  on Mono wasm — `Binders.WideSite` (a placeholder the LINQ `SiteHoister` rewrites) and the
  packed `Func<obj[], obj>` of `lambdaOver` in `TranslateMembers.fs` exist for that.
- .NET SDK 10 or later (net10.0 targets, the `.slnx`); SDK 8 and 9 are not supported (end of
  support November 2026). Their compilers build the tests in the `compilers` job only as a canary
  for behaviour that has been stable across compiler versions. Caller info
  is on `Delay`, never `Run`: on `Run` it made the 8/9 compilers warn FS3511 on every block in
  Debug (#126) and makes 11.0's fail FS2014 on an inline call in one (#235, dotnet/fsharp#20836).
  The block's file and line come from a probe on a miss (`DlrLocation`).
- `Tests.ClosureBuilder` (`Tests/ClosureBuilder.fs`) is `DlrBuilder` without its
  `if __useResumableCode` branch: `Run` always calls `DlrRun.Closure`, so `-p:DlrClosures=true`
  runs the suite on the closure path in Debug and Release. Every test file opens it after
  `FSharp.Interop.Dlr` under `#if DLR_CLOSURES` (copy the three lines into a new test file;
  `generate-shapes.fsx` writes them into `Shapes.fs`); the analyzer is off for that build (it
  knows blocks by `DlrBuilder`). Its members are pinned equal to `DlrBuilder`'s
  (`Tests/Resumable.fs`), so a change to the builder's signatures goes into both.
- Binary API: `DlrBuilder.Run` and `Delay` are `inline`, so the public members their bodies call
  (`DlrRun`, `DlrData`, `DlrReader`, `DlrLocation`) are compiled into consumers, and a Debug
  consumer calls `Delay` itself; changing one is `MissingMethodException` in an already-built
  consumer. `dotnet pack` validates against the last nuget.org release
  (`PackageValidationBaselineVersion` in `FSharp.Interop.Dlr.fsproj`); an intentional break goes in
  `FSharp.Interop.Dlr/CompatibilitySuppressions.xml` (written by
  `-p:GenerateCompatibilitySuppressionFile=true`) and the release notes. On each release that
  reaches nuget.org, move the baseline to it and clear that file.
- FSharp.Core floor 10.1.201 (older converters reject `Sequential`/`PropertySet`, so unit blocks,
  mutables and `let rec` fail there; the test projects run at the floor); netstandard2.0 has no `Architecture.Wasm` or Reflection.Emit inbox
  (`System.Reflection.Emit.Lightweight` is referenced for it).
- Benchmarks: only on a quiet machine (load average under 4; a busy one skews every cell).
  `Benchmarks/bench.sh docs` regenerates `docs/benchmarks.md`, then `bench.sh compare` flags the
  `dlr { }` cells over 25% slower or allocating more than the committed file (and prints the
  other columns' drift). Allocations on the hot paths are pinned exactly in `Tests/HotPath.fs`,
  which the gate runs; timings cannot be, so they are compared, not asserted. So in the docs a
  byte count may be written out where `Tests/HotPath.fs` pins it, but a timing is relative ("a few
  nanoseconds more", "several times") and points at its `docs/benchmarks.md` row: nothing checks a
  hand-written figure, and `bench.sh compare` reads only the generated tables.
- Copilot: one automatic review per new PR, none on later pushes; never request one while
  waiting (15–20 min); address via `check-review`, verifying each claim with a test first.
- Issues are the backlog; the API decisions above came from them.
