# FSharp.Interop.Dlr

[![CI](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml/badge.svg)](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml)
[![Tests](https://img.shields.io/badge/tests-270%20passed-brightgreen.svg?style=flat)](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml)
[![Line coverage](https://img.shields.io/badge/line%20coverage-93%25-brightgreen.svg?style=flat)](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml)
[![Branch coverage](https://img.shields.io/badge/branch%20coverage-87%25-green.svg?style=flat)](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](License.txt)

Experimental: not on NuGet yet, and the spelling of the API may still change.

**C#'s `dynamic`, for F#.** Inside a `dlr { }` block, `x?Name`, `x?Name(a, b)` and `x?Name <- v`
compile to what C# emits for `d.Name`, `d.Name(a, b)` and `d.Name = v` on a `dynamic`: one
Microsoft.CSharp call site per operation, created once, dispatching on the target's runtime type.
Same binder, same behaviour: C# overload resolution, named arguments, implicit conversions,
`ExpandoObject` / `DynamicObject` / `IDynamicMetaObjectProvider`, and `RuntimeBinderException`
when a bind fails. A block costs a few nanoseconds over C# `dynamic` after its first call and
allocates nothing itself (the numbers are under [Measured](#measured)).

```fsharp
open FSharp.Interop.Dlr
open Newtonsoft.Json.Linq

/// An order as Newtonsoft parsed it, and the pricing plugin the host loaded — both `obj`.
[<ReflectedDefinition>]                       // on the function that holds the blocks (see below)
let invoice (order: obj) (pricing: obj) =
    let customer: string = dlr { return order?customer?name }
    let city: string = dlr { return order?customer?address?city }
    let first: decimal = dlr { return order?lines |> Dlr.item 0 |> Dlr.get "price" }
    let total: decimal = dlr { return pricing?Total(order?lines, Dlr.named {| currency = "EUR" |}) }
    dlr { order?status <- "invoiced" }
    let discount: decimal =
        dlr {
            let mutable sum = 0m
            for line in (order?lines : JArray) do sum <- sum + (line?price : decimal)
            return pricing?Discount(customer, sum)
        }
    customer, city, first, total, discount
```

Chained gets converted to the inferred type, an index and a get in a pipeline, a call with a named
argument, a set, a loop, one block's result feeding another — each `dlr { }` a few nanoseconds
after its first call. The recursive step can be dynamic too:

```fsharp
[<ReflectedDefinition>]
let depth (root: obj) : int =
    dlr {
        let rec depth (node: obj) : int = if isNull node then 0 else 1 + depth node?child
        return depth root
    }
```

Both run as written in `Tests/Readme.fs`; every form is in [docs/syntax.md](docs/syntax.md).

Targets `netstandard2.0` and `net10.0`; depends on FSharp.Core ≥ 10.1.201 — its expression
converter is the first that handles a block's statements (any compiler can reference that
package; on an older SDK, set the `FSharp.Core` package version in the app).

The suite also runs against real dynamic targets — Newtonsoft `JObject`, IronPython, Python.NET,
Dapper rows over SQLite, ClearScript V8 — and on browser-wasm.

## Scope

`dlr { }` is C# `dynamic` for F#, and the scope follows from that in four steps:

1. **Parity with C# `dynamic`.** Everything C# can write with a `dynamic` operand has a spelling
   here — member get/set/invoke, indexers, operators, conversions, named and generic arguments,
   `+=`/`-=`, constructors and static overloads chosen by an argument's runtime type — through
   the same Microsoft.CSharp binders, so it binds what C# binds and fails where C# fails. C#'s
   restrictions are ours — extension methods, explicit interface members, accessibility, no AOT:
   [docs/restrictions.md](docs/restrictions.md).

2. **F# values C#'s binder does not understand.** F# code passes things C# never produces:
   function values (`FSharpFunc`) where C# has delegates, optional parameters compiled as
   `FSharpOption` with no `[Optional]`, records and unions with structural equality but no
   `op_Equality`. For those, the library adds binding rules of its own in that seam — only where
   C#'s binder would fail or bind against F#'s expectation, never changing what C# binds
   correctly. The five places, [listed](docs/restrictions.md#five-places-it-goes-beyond-c).

3. **What F#'s spelling exposes.** The F# forms are more general than C#'s syntax in a few
   places, and the library follows through rather than restricting them: `?` takes a string, so
   a member name can be a variable; `Dlr.typeArgsOf` takes a list, so type arguments can be
   run-time values; a tuple applies as several arguments, as in F#'s own method calls. Each is
   cached per call site so it costs a lookup, not a bind. Every form: [docs/syntax.md](docs/syntax.md).

4. **Mindful of speed.** It uses the same call sites C# does, bound once, and tries to stay in
   that neighbourhood; [docs/benchmarks.md](docs/benchmarks.md) has the numbers.

Outside the scope: reaching members the binder would not (a static-member-access API, private
members beyond the accessibility rules), reflection conveniences, and language features
`dynamic` has no counterpart for. If something is awkward in F# but C# `dynamic` cannot do it
either, the answer is usually a static call.

## Installing

Every push to `master` publishes `FSharp.Interop.Dlr` and `FSharp.Interop.Dlr.Analyzers` to the
ekonbenefits GitHub Packages feed, versioned by MinVer (`1.0.0-alpha.0.<height>` until a `v1.0.0`
tag). GitHub Packages needs a token even to read (a PAT with `read:packages`):

```
dotnet nuget add source https://nuget.pkg.github.com/ekonbenefits/index.json \
  --name ekonbenefits --username <github-user> --password <token> --store-password-in-clear-text
dotnet add package FSharp.Interop.Dlr --prerelease
```

## Where the attribute goes

The library reads a block's body from the reflected definition of the function or member around
it, so that function carries `[<ReflectedDefinition>]`:

```fsharp
[<ReflectedDefinition>]
let total (rows: obj) : decimal = dlr { return rows?Sum("Amount") }

type Report(data: obj) =
    [<ReflectedDefinition>]
    member _.Total: decimal = dlr { return data?Total }
```

On the function or member. A module- or type-level attribute also works, but it stores a
quotation of everything under it, and ordinary F# often has no quotation form — inner generic
functions, `byref`s, `Span` — so prefer the binding. If the function around a block cannot be
quoted, move the block into the smallest function that can.

Without the attribute the first call raises a `DlrTranslationException` that says so; the
[analyzer package](FSharp.Interop.Dlr.Analyzers/README.md) reports it at build time instead
(`DLR001`, with a fix), along with a marker used outside any block (`DLR002`), two blocks on
one line (`DLR003`), a block in an `inline` function (`DLR004`: it fails in Release, where
the function is expanded into its callers), a marker out of its place inside a block, such as
`Dlr.named` anywhere but in a call's arguments (`DLR005`), and a member whose reflected
definition FSharp.Core cannot decode (`DLR006`: it holds `typeof<System.Void>`). Why the block is not simply quoted by the compiler, sparing the attribute:
tried and [scrapped](https://github.com/ekonbenefits/FSharp.Interop.Dlr/issues/60) — a quotation
literal costs ~7 µs per evaluation and carries no calling type, so `internal` members would not
bind.

One block per source line. Blocks in generic functions and members work (one site per
instantiation); so do nested blocks, blocks inside `task { }` / `async { }`, and F# Interactive.

## Syntax

The common forms; [docs/syntax.md](docs/syntax.md) has every one, with what each binds to.

| | |
| --- | --- |
| `x?Name` | get, converted to the inferred type |
| `x?Name(a, b)` · `x?Name()` | call; arguments keep their static types, `box a` dispatches on the runtime type |
| `x?Name(a, Dlr.named {\| p = v \|})` · `Dlr.namedOf kw` · `Dlr.argsOf xs` | named arguments, as a record literal or from data; positional ones from data |
| `x?Name <- v` | set |
| `x \|> Dlr.item i` · `x \|> Dlr.setItem (i, j) v` | indexers |
| `x \|> Dlr.get "Name"` · `Dlr.invoke "Name" (a, b)` · `Dlr.set "Name" v` | the same three with the target last, for pipelines |
| `?+?` `?-?` … `?=?` `?<?` … | operators, converted to the inferred type (comparisons to `bool`) |
| `Dlr.cast<T> x` · `Dlr.implicit x` | explicit / implicit conversion |
| `Dlr.Static<T>.Overloads?Name(a)` · `Dlr.new'<T>(a)` | static overload / constructor chosen by the arguments' runtime types |
| `let f: int -> int -> int = dlr { return x?Add }` | a member read as an F# function |
| `Dlr.call f (a, b)` · `Dlr.call f` typed `A -> R` | invoke the value itself, the `?` of values; read as a function, it is one |
| `f \|> Dlr.apply (a, b)` | the same, target last for pipelines |

A target need not be `obj`: `let w = Widget()` then `w?Count` upcasts it and binds on the runtime
type as `box w` would. A member name may be a variable (`(?) x name`), and so may the type-argument list
(`Dlr.typeArgsOf ts`); each distinct value gets its own call sites, cached per site. Around the
markers, ordinary F#: `let`, `let rec`, `let mutable`, `use`, `if`, `match`, `for`, `while`,
`try`, lambdas, `sprintf`. A `RuntimeBinderException` can be caught inside the block.

## How it works

`dlr { … }` desugars to `dlr.Run(dlr.Delay(fun () -> …), file, line)`, and `Run` is resumable
code in `task { }`'s shape: the compiler turns each block into a struct state machine whose
fields are the captured variables — the machine is never run; its type identifies the block and
its fields hold the values. On the first call the body is found in the enclosing
`[<ReflectedDefinition>]`, [translated](docs/translation.md) into an expression tree with one
[`CallSite` per operation](docs/call-sites.md) baked in as a constant, and compiled to a delegate
over the machine, cached in a [static slot per machine type](docs/caches.md), so a call is a
field read and an invoke: no closure, no `GetType()`, no lookup. The few nanoseconds left over
C# `dynamic` are that invoke ([pipeline](docs/pipeline.md#one-call-on-the-hot-path) says why
they stay). Invocation sites use C#'s binder wrapped in
[one that also applies F# function values](docs/binders.md), as DLR rules per runtime type.

[docs/internals.md](docs/internals.md) indexes the full picture: every cache, every site and its
argument flags, the F#-aware binders, and what the translator assumes about the compiler.

## Measured

The same call each way it can be made, steady state (Release, net10.0, Apple Silicon): a `dlr { }`
call costs a few nanoseconds over C# `dynamic` — the same Microsoft.CSharp call sites, reached
through a struct state machine — and orders of magnitude under the reflection-based
FSharp.Interop.Dynamic. `Benchmarks/bench.sh docs` regenerates this table and the full
[docs/benchmarks.md](docs/benchmarks.md) (every suite, allocations, real targets).

<!-- benchmarks:start -->
| ns per call | static | C# `dynamic` | **`dlr { }`** | reflection (cached) | FSharp.Interop.Dynamic |
| --- | ---: | ---: | ---: | ---: | ---: |
| method call `w.Add(i, 1)` | 1.2 | 7.5 | **11.6** | 37.3 | 7,788 |
| property get `w.Count` | 0 | 6.9 | **10.9** | 12.5 | 4,004 |
<!-- benchmarks:end -->
