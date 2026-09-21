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

- The builder is resumable code (`inline` members, `ResumableCode<'D, 'T>` types; `Run` is
  `__stateMachine` in `task`'s shape): in Release each block is a struct state machine and its
  type keys `Machines<'SM, 'T>`; in Debug `__useResumableCode` is false and `DlrRun.Closure`
  unwraps the Delay closure (field `delayed` of the delegate's target) into the closure path.
  Both paths must stay correct — the suite runs in both. Every `dlr { }` compiles statically in
  Release except one shape the compiler declines silently (no FS3511): a function-typed result
  applied on the spot, `(dlr { … } : unit -> R) ()`, which takes the closure path — with a
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
  temporaries, a splat list, a tuple, a computed key) go through `sequenced` in `Translate.fs`,
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
- Binder rules go before C#'s only where C# would bind *wrongly* rather than fail (structural
  `==`, a `Delegate`-typed slot); otherwise ours is C#'s error suggestion. See `new-binder`.
- wasm: a nested non-capturing lambda loses its arguments on Mono's interpreter, and
  `FuncConvert` wrappers do too; `capturing` in `Translate.fs` and the typed wrappers in
  `Binders.fs` exist for that — do not "simplify" them away. A delegate type emitted at run
  time (a site or per-key delegate past 16 type parameters, i.e. 14 arguments) must never be
  named in a quotation: FSharp.Core's checks call `Assembly.ReflectionOnly` on it, unimplemented
  on Mono wasm — `Binders.WideSite` (a placeholder the LINQ `SiteHoister` rewrites) and the
  packed `Func<obj[], obj>` of `lambdaOver` in `Translate.fs` exist for that.
- FSharp.Core floor 10.1.201 (older converters reject `Sequential`/`PropertySet`, so unit blocks,
  mutables and `let rec` fail there; the test projects run at the floor); netstandard2.0 has no `Architecture.Wasm` or Reflection.Emit inbox
  (`System.Reflection.Emit.Lightweight` is referenced for it).
- Copilot: one automatic review per new PR, none on later pushes; never request one while
  waiting (15–20 min); address via `check-review`, verifying each claim with a test first.
- Issues are the backlog; the API decisions above came from them.
