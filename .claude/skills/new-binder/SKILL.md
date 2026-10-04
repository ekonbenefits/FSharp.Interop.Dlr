---
name: new-binder
description: Checklist for adding or changing an F#-aware DLR binder (Seam.fs) in FSharp.Interop.Dlr — rule shape, C#-first vs ours-first, parallel paths, required tests, docs. Use for issues like structural operators (#20) or delegate conversion (#21).
---

# Adding an F#-aware binder

A binder rule belongs in the *seam* (`docs/binders.md`): where C# fails or binds against F#'s
expectation on what F# hands it. The membership test: would the rule change what C# binds for
C#'s own inputs? Then it is not a seam rule (the structural-`==` exception is already taken).

Read `docs/binders.md` and `docs/call-sites.md` first (index: `docs/internals.md`); the library's files, in compile order: `Runtime.fs`
(`DlrRuntime`), `Reflection.fs` (`Accessibility`, `Conversions`, `Tuples`, `DelegateMembers`,
`Signatures`, `Emit`), `Functions.fs` (`FunctionShapes`, the function ↔ delegate conversions,
`FunctionMember`), `Seam.fs` (`Fallback`, `Seam`, the binders), `SiteCaches.fs`, and
`Binders.fs` (the `Binders` module that `Translate.fs` calls to emit `siteCall` nodes); the generated delegate/function adapter types are
in `Adapters.fs` (`generate-adapters.fsx`).

## Shape of a binder

- Subclass the matching `System.Dynamic` binder (`InvokeMemberBinder`, `BinaryOperationBinder`,
  …), wrapping the C# binder from `Microsoft.CSharp.RuntimeBinder.Binder.*` that the `Binders`
  module builds. Construct it in a `private smartX` function next to the C# one and only when
  the call qualifies (positional, non-generic); otherwise hand back C#'s binder unchanged.
- **Rules, never exceptions.** Return a `DynamicMetaObject` whose `Restrictions` pin the runtime
  types you inspected (`BindingRestrictions.GetTypeRestriction(expr, LimitType)` for each
  operand; `GetInstanceRestriction(expr, null)` for null). A rule is cached per site, so the
  site stays polymorphic and costs ~30 ns after the first call. Throwing in the binder is a
  per-call cost of microseconds and defeats the cache.
- Decide by `LimitType` (runtime type of an `obj` arg, static type of a typed arg), which is
  the site's own restriction key. Declared member types come from reflection with the
  `Accessibility` rules and the `context` type (the declaring type of the member containing the
  block); never `GetMembers()` public-only.
- If a meta-object has no value (`HasValue = false`) and you need one, return `Defer(...)`.
- **Order:** C# first, ours as `errorSuggestion` — *unless* C# would silently succeed with the
  wrong meaning (reference `==` on records, #20). Then ours first for the types where that
  happens, restricted so everything else still reaches C#.
- Delegates emitted through `Expression.Lambda`/`NewDelegate` need concrete delegate types:
  `Expression.GetDelegateType` (any arity) rather than `Func<…>` literals.

## Parallel paths — every behaviour needs all of them

The misses caught in review were always "handled here, not there". Check each:

- `FSharpInvokeMemberBinder` (`x?M(args)`), `FSharpReadOrInvokeBinder` (`unit -> R` read),
  `FSharpInvokeBinder` (`Dlr.call` / `Dlr.apply` / a value invoked), `FunctionMember`/`FunctionBuilder`
  (member read as `A -> B -> R`).
- Result discarded (`ResultDiscarded`, void site) vs converted result vs `obj` result.
- Typed args (`UseCompileTimeType`) vs `obj` args vs literals (`Constant`).
- CLR target vs `DynamicObject`/`ExpandoObject` target (`FallbackInvoke` after the dynamic
  object produced the member).
- Arities: 0, 1, 2, 5, >5 (the typed helpers stop at five; past it `FunctionBuilder` compiles the function).
- Accessibility: public, internal, protected, private from inside the declaring type.
- F# optional parameters (`Fallback.tryCall`) as the error suggestion where a method
  of that name exists.

## Tests (AnyUnit `[<Fact>]` + FsUnit `should`, module under `[<ReflectedDefinition>]`)

Put fixtures in `Tests/Fixtures.fs`, tests in the matching `Tests/*.fs` (new file: add it to
`Tests/Tests.fsproj` **and** `Tests.Wasm/Tests.Wasm.fsproj`, which link the same sources).
Minimum set for a binder change:

- The happy path per parallel path above, including a `unit` result.
- A **polymorphic** test: one site in a loop over alternating runtime types (`Tests/Polymorphic.fs`
  style), including a real `DynamicObject`.
- A type that has the CLR operator/member C# already handles — proves our rule does not
  shadow it.
- The failure: what still throws (`RuntimeBinderException`) or is a `DlrTranslationException`,
  with a message assertion. A new `DlrTranslationException` also gets an analyzer check
  (`FSharp.Interop.Dlr.Analyzers/ReflectedDefinitionAnalyzer.fs`, `misplacedMarkers` for DLR005,
  with a test in `Analyzers.Tests`) — the shape it rejects is in the typed tree, so the build can
  report it; the run-time test then carries `// fsharpanalyzer: ignore-line-next DLR005`. Only
  a shape the typed tree cannot show (a run-time value) is exempt; say so in the PR.
- If a restriction in `docs/restrictions.md` is lifted, delete its pin in `Tests/Restrictions.fs`.

Gate: `dotnet test -c Debug`, `dotnet test -c Release` (optimizer inlining differs), and the
`Tests.Wasm` run (interpreted runtime; expression compilation differs) — commands in CLAUDE.md.

## Docs

- `docs/binders.md`: the binder's rule, its order relative to C#, its restrictions; `docs/call-sites.md` if it adds a site shape.
- `docs/restrictions.md`: the "five places it goes beyond C#" list gets the new capability in
  one bullet; the restrictions list loses the lifted one. `docs/syntax.md` if it adds a form.
  The README stays short — the owner asked for a less wordy README more than once.
- Benchmarks in the README are Release, Apple Silicon; if you touch the hot path, re-measure
  before quoting numbers.
