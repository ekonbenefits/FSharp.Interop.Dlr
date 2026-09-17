# FSharp.Interop.Dlr

`dlr { }` = C# `dynamic` for F#: markers (`?`, `Dlr.*`) are read from the block's
`[<ReflectedDefinition>]` body and compiled once per site into Microsoft.CSharp call sites.
Design and binder details: `docs/internals.md`. Skills: `check-review`, `new-binder`.

## Gate before pushing

```
dotnet test -c Debug
dotnet test -c Release            # Release inlining has broken things Debug passed
dotnet build Tests.Wasm -c Release && (cd Tests.Wasm/bin/Release/net10.0-browser/wwwroot && bun runtests.mjs)
```
Analyzer tests (`Analyzers.Tests`) run as part of `dotnet test`. CI treats warnings as errors.
CI (`build.yml`) is manual-only (`workflow_dispatch`) while the repo is private and out of
Actions minutes, so the local gate above is the gate; run the workflow by hand to publish.

## Conventions

- Tests: AnyUnit xunit style, `[<Fact>]` + FsUnit `should`; test modules are
  `[<ReflectedDefinition>]`; `Tests.Wasm` links the same files, so add new test files to both
  fsproj files.
- No lambda-form API, no static-member-access hack, `Dlr.cast` is conversion only (explicit
  interfaces: static cast).
- Markers are `[<MethodImpl(NoInlining)>]`; FSharp.Core floor 6.0.1.
- Copilot reviews: never request one while waiting (15–20 min); address via `check-review`.
- Commit only when asked; prerelease packages publish from every master push.
