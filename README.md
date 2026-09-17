# FSharp.Interop.Dlr

[![CI](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml/badge.svg)](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml)
[![Tests](https://img.shields.io/badge/tests-139%20passed-brightgreen.svg?style=flat)](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml)
[![Line coverage](https://img.shields.io/badge/line%20coverage-92%25-brightgreen.svg?style=flat)](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml)
[![Branch coverage](https://img.shields.io/badge/branch%20coverage-91%25-brightgreen.svg?style=flat)](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](License.txt)

**C#'s `dynamic`, for F#.** Inside a `dlr { }` block, `x?Name`, `x?Name(a, b)` and `x?Name <- v`
compile to what C# emits for `d.Name`, `d.Name(a, b)` and `d.Name = v` on a `dynamic`: one
Microsoft.CSharp call site per operation, created once, dispatching on the target's runtime type.
Same binder, same behaviour: C# overload resolution, named arguments, implicit conversions,
`ExpandoObject` / `DynamicObject` / `IDynamicMetaObjectProvider`, and `RuntimeBinderException`
when a bind fails. A block costs about 25 ns after its first call.

```fsharp
open FSharp.Interop.Dlr

[<ReflectedDefinition>]                       // on the function that holds the blocks (see below)
let demo (w: obj) (root: obj) =
    let n: int = dlr { return w?Count }                                           // get + convert
    let s: string = dlr { return w?Greet("Hi", Dlr.named {| name = "Jay" |}) }   // call, named arg
    dlr { w?Count <- 9 }                                                          // set
    let v: int = dlr { return (Dlr.idx w).[1, 2] }                                // index
    let name: string = dlr { return root |> Dlr.get "Child" |> Dlr.get "Name" }   // pipelines
    let depth: int =                                                              // recursion
        dlr {
            let rec depth (node: obj) : int =
                if isNull node then 0 else 1 + depth node?Child
            return depth root
        }
    n, s, v, name, depth
```

Targets `netstandard2.0` and `net10.0`; needs FSharp.Core ≥ 6.0.1 (and Microsoft.CSharp on
netstandard2.0). Experimental.

## Installing

Every push to `master` publishes `FSharp.Interop.Dlr` and `FSharp.Interop.Dlr.Analyzers` to the
ekonbenefits GitHub Packages feed, versioned by MinVer (`1.0.0-alpha.0.<height>` until a `v1.0.0`
tag). GitHub Packages needs a token even to read (a PAT with `read:packages`):

```
dotnet nuget add source https://nuget.pkg.github.com/ekonbenefits/index.json \
  --name ekonbenefits --username <github-user> --password <token> --store-password-in-clear-text
dotnet add package FSharp.Interop.Dlr --prerelease
```

## The `[<ReflectedDefinition>]` rule

A block's body is read from the reflected definition of the function or member containing it, so
that function needs `[<ReflectedDefinition>]`. Put it on that one binding — not the module: the
attribute makes the compiler store a quotation of everything it covers, and ordinary F# often has
no quotation form (inner generic functions, byrefs, `Span`), so a module-wide attribute breaks
unrelated code. An enclosing type or module attribute does work when everything in it is quotable.

```fsharp
[<ReflectedDefinition>]
let total (rows: obj) : decimal = dlr { return rows?Sum("Amount") }

type Report(data: obj) =
    [<ReflectedDefinition>]
    member _.Total: decimal = dlr { return data?Total }
```

Without it, the first call raises a `DlrTranslationException` that says so. The
[`FSharp.Interop.Dlr.Analyzers`](FSharp.Interop.Dlr.Analyzers/README.md) package reports it at
build time instead (`DLR001`, with a fix), and reports a `?` or `Dlr.*` used outside any block
(`DLR002`), through `FSharp.Analyzers.Build` and in Ionide.

One `dlr { }` per source line. Blocks in generic functions and members work (one site per
instantiation); so do nested blocks, blocks inside `task { }` / `async { }`, and F# Interactive.

## Syntax

A member name may be a variable: the site then compiles one delegate per distinct name on first
use, and a repeated name costs a dictionary lookup.

| Syntax | Binder |
| --- | --- |
| `x?Name` | GetMember, then Convert to the inferred type |
| `x?Name(a, b)`, `x?Name()` | InvokeMember. Arguments keep their static type; `obj` arguments dispatch on the runtime type; literals get C#'s constant conversions (`5` to `byte`, `0` to an enum, `null` to any reference type). A member holding an F# function value (curried or tupled) is applied when the binder cannot invoke it |
| `x?Name(a, Dlr.named {\| p = v \|})` | named arguments (a bare anonymous record is one positional argument) |
| `x?Name(Dlr.typeArgs<A, B>(), a)` | explicit type arguments (up to four, first); otherwise inferred from the arguments as in C# |
| `x?Name` typed `A -> B -> R` | a curried F# function that invokes the member when fully applied — a method, a delegate or an F# function alike — so `let add: int -> int -> int = dlr { return w?Add }`, then `add 1 2` or `add 1` partially; `A * B -> R` calls with a tuple; `unit -> R` reads a property or calls a parameterless method |
| `x?Name <- v` | SetMember |
| `(?) x name`, `((?) x name)(a)`, `(?<-) x name v` | the same three as plain function applications |
| `x \|> Dlr.get "Name"` | GetMember, target last, for pipelines; applied to arguments it invokes, like `?` |
| `x \|> Dlr.invoke "Name" (a, b)` | InvokeMember, target last |
| `x \|> Dlr.set "Name" v` | SetMember, target last |
| `x \|> Dlr.addAssign "Name" v`, `x \|> Dlr.subtractAssign "Name" v` | C#'s `+=` / `-=`: an IsEvent site picks the event accessor (`add_` / `remove_`) or read-modify-write |
| `x \|> Dlr.call (a, b)`, `x \|> Dlr.call ()` | Invoke the object itself: a delegate, a callable dynamic object, or an F# function value |
| `(Dlr.idx x).[i]`, `(Dlr.idx x).[i, j] <- v` | GetIndex / SetIndex, up to four indexes |
| `?+? ?-? ?*? ?/? ?%? ?&&&? ?\|\|\|? ?^^^? ?<<<? ?>>>?` | BinaryOperation, then Convert |
| `?=? ?<>? ?<? ?>? ?<=? ?>=?` | BinaryOperation, then Convert to `bool` |
| `Dlr.neg x`, `Dlr.not x`, `Dlr.complement x` | UnaryOperation, then Convert |
| `Dlr.cast<T> x` | explicit Convert (a C# cast) |
| `Dlr.implicit x` | implicit Convert to the inferred type: widening, `op_Implicit`, `TryConvert` |

Around them, ordinary F#: `let`, `let rec`, `use`, `if`, `for`, `while`, `try … with`,
`try … finally`, and any code that quotations can express. Loop bodies reuse the block's call
sites; a `RuntimeBinderException` can be caught inside the block. As in `async { }`, a
`let mutable` cannot be captured by a loop or `try` body — use a `ref`. Calling any operator or
`Dlr.*` marker outside a block throws `InvalidOperationException`; they exist only to be quoted.

## The same restrictions as C# `dynamic`

Same binder, same limits — each pinned by a test in `Tests/Restrictions.fs`:

- **Extension methods** are not found; the binder sees only the target's own members.
- **Static members** cannot be reached through an instance.
- **Accessibility is the calling type's**: `private` binds only inside the declaring type,
  `internal` anywhere in the assembly. F# `private` compiles to IL `internal`.
- **Lambdas passed as arguments need a delegate type** (`Func<int, int>(fun x -> …)`); an F#
  function value is an `FSharpFunc`, not a `Func`. (A member that *holds* an F# function is
  fine: the binder cannot invoke it, so the library reads and applies it — the one place it goes
  beyond C#.)
- **No compile-time checking**: a misspelt member or wrong arity is a `RuntimeBinderException`
  at the call.
- **Target and result are `obj`**, so value types box there; arguments do not. `byref` and
  `Span` cannot cross a dynamic operation.
- **Generic type arguments** must be inferable from the arguments, or given with `Dlr.typeArgs`.
- **F# optional parameters (`?arg`)** cannot be omitted: they are plain `FSharpOption<'T>`
  parameters with no `[Optional]` metadata (a bare value still converts via `op_Implicit`).
  `[<Optional; DefaultParameterValue>]` parameters are optional, as in C#.
- **No NativeAOT, no trimming.** The runtime binder, `LambdaExpression.Compile()` and the
  reflection that finds bodies and closure fields all need a JIT; the assembly is marked
  `IsAotCompatible=false` / `IsTrimmable=false`. Interpreted (non-AOT) browser-wasm works, and CI
  runs it, just not at JIT speed.

## How it works

1. With no `Quote` member, `dlr { … }` desugars to `dlr.Run(dlr.Delay(fun () -> …), file, line)`.
   `Delay` returns the closure unevaluated; its compiler-generated type is unique to the block and
   its fields are the captured variables.
2. On the first call, `Discover` finds the block's body in the enclosing `[<ReflectedDefinition>]`
   (decoded once by FSharp.Core) by the baked line number.
3. `Translate` turns free variables into reads of the closure's fields and each dynamic operation
   into a call on a `CallSite<_>` embedded as a constant, then compiles the tree to a
   `Func<obj, 'T>` cached by closure type. Invocation sites use C#'s binder wrapped in one that
   also knows F# function values (`FSharpInvokeMemberBinder`): the decision is a DLR rule
   restricted to the runtime type, so a site that sees several kinds of target keeps one cached
   rule per kind.

Why not `Quote` the block? FSharp.Core rebuilds a quotation literal on every evaluation, ~4–10 µs
each, with no cache; reflected definitions are decoded once.

**Compiler assumptions.** The desugaring, caller-info arguments, `[<ReflectedDefinition>]` and
`LeafExpressionConverter` are specified F#. The closure class shape is not: fields named after the
captured variables (`this` as `this`, a `let mutable` as an `FSharpRef`), nesting in the module or
`<StartupCode$…>` type, generic parameters named as the member's, and the Release optimizer
inlining constants and once-called local functions (resolved from the reflected body). If any of
that changes, the first call raises `DlrTranslationException` — nothing binds silently wrong — and
CI builds with the .NET 8, 9 and 10 SDKs, Debug and Release, to catch it first.

## Measured

Release, net10.0, Apple Silicon, 5M-call average after warm-up:

| | ns/call |
| --- | --- |
| `dlr { return w?Count }` | 24 |
| `dlr { return w?Add(i, 1) }` | 29 |
| `dlr { for x in items do … w?Add(x, i) … }`, 100 items | 1 690 per block, ≈17 per iteration |
| FSharp.Interop.Dynamic `w?Count` / `w?Add(i, 1)` | ~4 100 / ~7 800 |
| reflection, cached `PropertyInfo.GetValue` / `MethodInfo.Invoke` | 17 / 63 |
| static `w.Count` | 4 |
