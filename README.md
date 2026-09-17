# FSharp.Interop.Dlr

[![Build](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml/badge.svg)](https://github.com/ekonbenefits/FSharp.Interop.Dlr/actions/workflows/build.yml)

Experimental. A `dlr { }` computation expression in which the `?` operator (and friends) is
never executed. The block's `Delay` closure identifies the call site and carries the captured
variables; the block's body comes from the enclosing `[<ReflectedDefinition>]`; it is translated
once into a LINQ expression tree whose Microsoft.CSharp `CallSite`s are baked in as constants and
compiled to a `Func<closure, 'T>`. After the first call, a block costs one type-keyed lookup plus
the delegate: about 25 ns.

```fsharp
open FSharp.Interop.Dlr

[<ReflectedDefinition>]          // on the module, type or member that contains the dlr { } blocks
module Demo =

let w = box (Widget())
let n: int = dlr { return w?Count }                       // GetMember + Convert to int
let s: string = dlr { return w?Greet("Hi", Dlr.named {| name = "Jay" |}) }   // InvokeMember, named arg
dlr { w?Count <- 9 }                                       // SetMember
let sum: int = dlr { return (box 1) ?+? (box 2) }          // BinaryOperation
let v: int = dlr { return (Dlr.idx w).[1, 2] }             // GetIndex
dlr { (Dlr.idx w).[1, 2] <- v }                            // SetIndex
let name: string = dlr { return root |> Dlr.get "Child" |> Dlr.get "Name" }   // pipe order
let depth: int =                                           // recursion over a runtime-shaped graph
    dlr {
        let rec depth (node: obj) : int =
            if isNull node then 0 else 1 + depth node?Child
        return depth root
    }
```

Targets `netstandard2.0` and `net10.0`. Depends on FSharp.Core ≥ 6.0.1 and, on
netstandard2.0, Microsoft.CSharp.

## Prerelease packages

Every push to `master` publishes `FSharp.Interop.Dlr` at its MinVer version
(`1.0.0-alpha.0.<height>` until a `v1.0.0` tag exists) to the ekonbenefits GitHub Packages feed.
GitHub Packages needs a token even to read; a personal access token with `read:packages` does:

```
dotnet nuget add source https://nuget.pkg.github.com/ekonbenefits/index.json \
  --name ekonbenefits --username <github-user> --password <token> --store-password-in-clear-text
dotnet add package FSharp.Interop.Dlr --prerelease
```

## Not for NativeAOT or trimming

This is a JIT-only library, like C# `dynamic` itself:

- `Microsoft.CSharp.RuntimeBinder` requires dynamic code and unreferenced members (it is
  `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]` in .NET).
- The compiled block is a `LambdaExpression.Compile()`; without dynamic code it would fall back to
  the expression interpreter and lose the speed that is the point.
- Bodies come from `[<ReflectedDefinition>]` resources and captured values from closure fields,
  both found by reflection that a trimmer cannot see.

The assembly is marked `IsAotCompatible=false` / `IsTrimmable=false` so `dotnet publish` warns.

It does work on **browser-wasm in interpreted (non-AOT) mode**, which CI runs: Mono's interpreter
executes the expression tree through the expression interpreter rather than JIT-compiled code, so
it is correct there, just not at the numbers below.

## What is recognised inside `dlr { }`

| Syntax | Binder |
| --- | --- |
| `x?Name` | GetMember, then Convert to the inferred type |
| `x?Name(a, b)`, `x?Name()` | InvokeMember; args use their static F# type, `obj` args dispatch on runtime type, literals get C#'s constant conversions (`5` → `byte`, `0` → enum, `null` → any reference type) |
| `x?Name(a, Dlr.named {| p = v |})` | InvokeMember with named arguments; a bare `{| |}` is one positional argument |
| `x \|> Dlr.get "Name"`, `(x \|> Dlr.get "Add") (1, 2)`, `x \|> Dlr.invoke "Add" (1, 2)`, `x \|> Dlr.set "Name" v` | the same operations with the target last, for pipelines: `root \|> Dlr.get "Child" \|> Dlr.get "Name"`. `Dlr.get` invokes when applied, like `?` |
| `(?) x name`, `((?) x name)(a)`, `(?<-) x name v` with `name` a variable | the same binders, bound per distinct name at run time: the site holds one compiled delegate per name, made on first use, so a repeated name costs a dictionary lookup |
| `x?Name(Dlr.typeArgs<A, B>(), a)` | InvokeMember with explicit generic type arguments (up to four; marker goes first). Without it, type arguments are inferred from the argument types as in C# |
| `x?Name <- v` | SetMember |
| `x \|> Dlr.call (a, b)`, `x \|> Dlr.call ()` | Invoke: the object itself (a delegate, a callable dynamic object); chains after `Dlr.get` |
| `(Dlr.idx x).[i]`, `(Dlr.idx x).[i, j] <- v` (up to four indexes) | GetIndex / SetIndex; element type inferred from use |
| `?+? ?-? ?*? ?/? ?%? ?&&&? ?\|\|\|? ?^^^? ?<<<? ?>>>?` | BinaryOperation, then Convert |
| `?=? ?<>? ?<? ?>? ?<=? ?>=?` | BinaryOperation, then Convert to bool |
| `Dlr.neg x`, `Dlr.not x`, `Dlr.complement x` | UnaryOperation, then Convert |
| `Dlr.cast<T> x` | explicit Convert (a C# cast); `?` results convert implicitly on their own |
| `Dlr.implicit x` | implicit Convert of a value you already hold, to the type inferred from use (widening, `op_Implicit`, `TryConvert`) |

Plus `let`, `let rec` (including mutual recursion), `use`, `if`, sequencing, `for x in items do …`,
`while … do …`, `try … with`, `try … finally` and ordinary F# code. Loop bodies reuse the block's call sites across iterations; a failed dynamic
bind (`RuntimeBinderException`) can be caught inside the block. Blocks can be nested (an inner block compiles as part of the outer
one), can sit inside `task { }` / `async { }`, and work in F# Interactive scripts (mark the
module `[<ReflectedDefinition>]` as usual). As in `async { }`, a `let mutable`
cannot be captured by a loop or try body; use a `ref` or an object. Loops or `try` inside a lambda within the block (as opposed to at block
level) are not translated (`LeafExpressionConverter` limit).

Rules: the enclosing module, type or member must be `[<ReflectedDefinition>]` (a clear
`DlrTranslationException` says so otherwise); one `dlr { }` per source line (the body is located by
line inside the reflected definition). Blocks inside generic functions or members work; each
instantiation is its own site, compiled with the concrete types.
Calling any of the operators or `Dlr.*` markers outside `dlr { }` throws `InvalidOperationException`.
The `Dlr.*` markers exist only to give F# something it can type-check; `Named<'T>`, `Indexed<'T>` and
`TypeArgs` have no constructors and are never instantiated.

The binder's accessibility context is the type declaring the member the block sits in, as for C#
`dynamic`: a block inside a class can bind that class's non-public members.

## How it works

1. Without a `Quote` member, `dlr { … }` desugars to `dlr.Run(dlr.Delay(fun () -> …), file, line)`.
   `Delay` returns the closure unevaluated. Its compiler-generated type is unique to the block and
   its fields are the captured variables, named after them (`this` included; a captured
   `let mutable` is an `FSharpRef` cell). In Release the optimizer inlines constant locals and
   local functions instead of capturing them; those resolve from their `let` binding in the
   enclosing member's reflected body.
2. On the first call `Discover` finds the block's body: it reads the `[<ReflectedDefinition>]`
   quotations of the closure's declaring type and nested types (FSharp.Core caches those once
   decoded) and picks the `Run` call whose baked `CallerLineNumber` matches.
3. `Translate` strips the builder calls, turns each free variable into a read of the closure field
   of that name, and replaces each dynamic operation by `site.Target.Invoke(site, …)` where `site`
   is a `CallSite<_>` embedded as a `Value` — which `LeafExpressionConverter` turns into
   `Expression.Constant`. Literals stay constants. The result is compiled to `Func<obj, 'T>` and
   cached in `DlrCache` under the closure type.

## What it assumes about the compiler

Everything above the closure is specified F#: the computation-expression desugaring, caller-info
arguments, `[<ReflectedDefinition>]` and `LeafExpressionConverter`. What is *not* specified, and
what a future compiler could change, is the shape of the closure class F# generates for the
`Delay` lambda, which `Translate.captured` and `Discover` read:

- fields named after the captured variables, `this` as `this`, a captured `let mutable` as an
  `FSharpRef` field of the same name;
- closures nested in the enclosing module type, or in the file's `<StartupCode$…>` class for members
  of types declared in a namespace;
- for generic members, a closure class generic over the member's type parameters, under the same
  names;
- the Release optimizer inlining constants and local functions instead of capturing them (resolved
  from the `let` in the reflected body).

If any of that moves, the first call at a site raises `DlrTranslationException` naming the closure
and its fields; nothing binds silently wrong. CI builds the same source with the .NET 8, 9 and 10
SDKs (F# 8, 9 and 10) in Debug and Release so such a change is caught here first.

## Measured (Release, net10.0, Apple Silicon, 5M-call average after warm-up)

| | ns/call |
| --- | --- |
| `dlr { return w?Count }` | 24 |
| `dlr { return w?Add(i, 1) }` | 29 |
| `dlr { return w?Add(i, Dlr.named {| b = 1 |}) }` | 29 |
| `dlr { for x in items do … w?Add(x, i) … }`, 100 items | 1 690 per block, ≈17 per iteration |
| FSharp.Interop.Dynamic `w?Count` / `w?Add(i, 1)` | ~4 100 / ~7 800 |
| reflection, cached `PropertyInfo.GetValue` / `MethodInfo.Invoke(w, [\| i; 1 \|])` | 17 / 63 |
| reflection, `GetProperty` + `GetValue` / `GetMethod` + `Invoke` each call | 49 / 67 |
| static `w.Count` | 4 |

Why not quote the block instead (`Quote` in the builder)? Because FSharp.Core rebuilds a quotation
literal on every evaluation (`Expr.Deserialize40` re-binds every type and method by reflection, no
cache; even `<@ 1 @>` is ~4 µs), which made the quoted version ~10 µs per call. Reflected
definitions are decoded once, which is what makes this design work.
