<img src="icon.png" alt="" width="96" align="right">

# FSharp.Interop.Dlr

[![CI](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml/badge.svg)](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml)
[![Tests](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fekonbenefits%2FFSharp.Interop.Dlr%2Fbadges%2Ftests.json)](https://ekonbenefits.github.io/FSharp.Interop.Dlr/tests/)
[![Line coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fekonbenefits%2FFSharp.Interop.Dlr%2Fbadges%2Fline.json)](https://ekonbenefits.github.io/FSharp.Interop.Dlr/coverage/)
[![Branch coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fekonbenefits%2FFSharp.Interop.Dlr%2Fbadges%2Fbranch.json)](https://ekonbenefits.github.io/FSharp.Interop.Dlr/coverage/)
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

The recursive step can be dynamic too:

```fsharp
[<ReflectedDefinition>]
let depth (root: obj) : int =
    dlr {
        let rec depth (node: obj) : int = if isNull node then 0 else 1 + depth node?child
        return depth root
    }
```

Both run in `Tests/Readme.fs`; every form is in [docs/syntax.md](docs/syntax.md).

Targets `netstandard2.0` and `net10.0`; depends on FSharp.Core ≥ 10.1.201 — its expression
converter is the first that handles a block's statements. Build with .NET SDK 10: the SDK 8 and 9
compilers (both end of support in November 2026) are not supported — in Debug they warn FS3511
on every block. The app itself may run on anything `netstandard2.0` covers, .NET Framework
included.

The suite also runs against real dynamic targets — Newtonsoft `JObject`, IronPython, Python.NET,
Dapper rows over SQLite, ClearScript V8, PowerShell `PSObject`s, COM `IDispatch` on Windows
(`Scripting.FileSystemObject`, `WScript.Shell`, `Scripting.Dictionary`, ADO's `Recordset` events,
`Stream`, and `Connection` against SQL Server LocalDB for an `[out]`), and NLua through
a short `DynamicObject` adapter (`Tests/NLua.fs`, the pattern for any late-bound API without a DLR
face) — and on browser-wasm.

## Scope

`dlr { }` is C# `dynamic` for F#:

1. **Parity with C# `dynamic`**: the same binders, so it binds what C# binds and fails where C#
   fails, restrictions included ([restrictions](docs/restrictions.md)).
2. **F# values C#'s binder does not understand** (functions, F# optional parameters, structural
   equality): binding rules of its own only where C# would fail or bind against F#'s expectation
   ([the seam](docs/binders.md)).
3. **What F#'s spelling exposes**: a member name or type arguments from a variable, a tuple as
   several arguments, each cached per site ([syntax](docs/syntax.md)).
4. **Mindful of speed**: C#'s call sites, bound once ([benchmarks](docs/benchmarks.md)).

Outside the scope: reaching members the binder would not (a static-member-access API, private
members beyond the accessibility rules), reflection conveniences, and language features
`dynamic` has no counterpart for. If something is awkward in F# but C# `dynamic` cannot do it
either, the answer is usually a static call.

## Installing

```
dotnet add package FSharp.Interop.Dlr
dotnet add package FSharp.Interop.Dlr.Analyzers   # optional: build-time checks
```

Releases are on nuget.org. Every push to `master` also publishes a prerelease
(`<next>-alpha.0.<height>`) to the ekonbenefits GitHub Packages feed, which needs a token even to
read (a PAT with `read:packages`):

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
[analyzer package](FSharp.Interop.Dlr.Analyzers/README.md) reports it at build time instead, with
a fix, along with the other misuses it can see (`DLR002`–`DLR006`).

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
| `x?TryGetValue(k, Dlr.out)` · `Dlr.outAs<T> ()` · `x?Swap(Dlr.ref a, Dlr.ref b)` | `out` / `ref` parameters: outs returned as F# returns them (`let (ok: bool), (v: int) = …`; `Dlr.outAs` states an out's type where the shape is ambiguous), refs written back to a `let mutable` |
| `x \|> Dlr.addAssign "Click" handler` · `Dlr.subtractAssign` | `+=` / `-=`: an event handler (an F# function converts) or read-modify-write |
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

In Release the compiler turns each block into a struct state machine whose fields are its captured
variables (in Debug, where it builds none, the block's closure plays that part: a little slower,
the same results). On the first call the block's body is read from the enclosing
`[<ReflectedDefinition>]`, translated into an expression tree with one C# call site per operation,
and compiled to a delegate over that struct; every later call is a field read and an invoke.

[docs/internals.md](docs/internals.md) indexes the full picture: every cache, every site and its
argument flags, the F#-aware binders, and what the translator assumes about the compiler.

## Measured

Steady state, Release, net10.0, Apple Silicon. `Benchmarks/bench.sh docs` regenerates this table
and the full [docs/benchmarks.md](docs/benchmarks.md) (every suite, allocations, real targets).

<!-- benchmarks:start -->
| ns per call | static | C# `dynamic` | **`dlr { }`** | reflection (cached) | FSharp.Interop.Dynamic |
| --- | ---: | ---: | ---: | ---: | ---: |
| method call `w.Add(i, 1)` | 1.2 | 7.4 | **11.5** | 35.7 | 7,485 |
| property get `w.Count` | 0 | 6.7 | **10.8** | 12 | 3,942 |
<!-- benchmarks:end -->
