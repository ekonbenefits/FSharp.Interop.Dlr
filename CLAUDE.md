# FSharp.Interop.Dlr

`dlr { }` = C# `dynamic` for F#: markers (`?`, `Dlr.*`, `Dlr.Static<T>.Overloads`) are read from
the block's `[<ReflectedDefinition>]` body and compiled once per site into Microsoft.CSharp call
sites. Design and binder details: `docs/internals.md`. Skills: `check-review`, `new-binder`.

## Gate before pushing

```
dotnet build Tests/Tests.fsproj -c Debug -f net10.0 --no-incremental   # runs the analyzer over the tests
dotnet test -c Debug
dotnet test -c Release            # Release inlining has broken things Debug passed
dotnet build Tests.Wasm -c Release && (cd Tests.Wasm/bin/Release/net10.0-browser/wwwroot && bun runtests.mjs)
```

- The first line matters: the analyzer step runs only when `fsc` actually recompiles, so an
  incremental `dotnet test` can show green with the analyzer never having run (a prefix bug in
  it once shipped that way). `--no-incremental` forces it; `error DLR001`/`DLR002` fails the build.
- Analyzer tests (`Analyzers.Tests`) run as part of `dotnet test`; run their dll directly to see
  them alone. CI treats warnings as errors.
- CI (`build.yml`) is manual-only (`workflow_dispatch`) while the repo is private and out of
  Actions minutes, so this local gate is the gate; run the workflow from the Actions tab to
  publish a prerelease (on master it also refreshes the badges).
- A cold review by a general-purpose subagent (read the diff, verify every claim with a test,
  no edits) has caught things Copilot missed; worth one per non-trivial PR when asked.

## Conventions

- Tests: AnyUnit xunit style, `[<Fact>]` + FsUnit `should`; test modules are
  `[<ReflectedDefinition>]`; `Tests.Wasm` links the same files, so add new test files to both
  fsproj files. Tests needing real threads skip with `AnyUnit.IgnoreException` when
  `ProcessorCount < 2` (wasm) rather than passing vacuously.
- Markers: `[<MethodImpl(NoInlining)>]`, throw outside a block, and every one is in the
  `Errors.fs` outside-a-block test. New `Dlr` members are analyzer markers automatically; types
  in the `Dlr` module (`DlrModule` to FCS) too — never widen the analyzer's prefix past
  `FSharp.Interop.Dlr.DlrModule.` (`DlrCache`, `DlrRuntime`, `DlrBuilder` share the prefix).
- No lambda-form API; `Dlr.cast` is conversion only (explicit interfaces: static cast); no
  static property/field/event access — `Dlr.Static<T>.Overloads` is for calls only, a static
  property is `T.P` in plain F#.
- Binder rules go before C#'s only where C# would bind *wrongly* rather than fail (structural
  `==`, a `Delegate`-typed slot); otherwise ours is C#'s error suggestion. See `new-binder`.
- wasm: a nested non-capturing lambda loses its arguments on Mono's interpreter, and
  `FuncConvert` wrappers do too; `capturing` in `Translate.fs` and the typed wrappers in
  `Binders.fs` exist for that — do not "simplify" them away.
- FSharp.Core floor 10.1.201 (older converters reject `Sequential`/`PropertySet`, so unit blocks,
  mutables and `let rec` fail there; the test projects run at the floor); netstandard2.0 has no `Architecture.Wasm` or Reflection.Emit inbox
  (`System.Reflection.Emit.Lightweight` is referenced for it).
- Copilot: one automatic review per new PR, none on later pushes; never request one while
  waiting (15–20 min); address via `check-review`, verifying each claim with a test first.
- Commit only when asked. Issues are the backlog; the API decisions above came from them.
