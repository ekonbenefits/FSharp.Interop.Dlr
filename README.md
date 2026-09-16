# FSharp.Interop.Dlr

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
```

Targets `netstandard2.0` and `net10.0`. Depends on FSharp.Core ≥ 6.0.1 and, on
netstandard2.0, Microsoft.CSharp.

## Not for NativeAOT or trimming

This is a JIT-only library, like C# `dynamic` itself:

- `Microsoft.CSharp.RuntimeBinder` requires dynamic code and unreferenced members (it is
  `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]` in .NET).
- The compiled block is a `LambdaExpression.Compile()`; without dynamic code it would fall back to
  the expression interpreter and lose the speed that is the point.
- Bodies come from `[<ReflectedDefinition>]` resources and captured values from closure fields,
  both found by reflection that a trimmer cannot see.

The assembly is marked `IsAotCompatible=false` / `IsTrimmable=false` so `dotnet publish` warns.

## What is recognised inside `dlr { }`

| Syntax | Binder |
| --- | --- |
| `x?Name` | GetMember, then Convert to the inferred type |
| `x?Name(a, b)`, `x?Name()` | InvokeMember; args use their static F# type, `obj` args dispatch on runtime type |
| `x?Name(a, Dlr.named {| p = v |})` | InvokeMember with named arguments; a bare `{| |}` is one positional argument |
| `x?Name <- v` | SetMember |
| `(!?x)(a)` | Invoke |
| `(Dlr.idx x).[i]`, `(Dlr.idx x).[i, j] <- v` (up to four indexes) | GetIndex / SetIndex; element type inferred from use |
| `?+? ?-? ?*? ?/? ?%? ?&&&? ?\|\|\|? ?^^^? ?<<<? ?>>>?` | BinaryOperation, then Convert |
| `?=? ?<>? ?<? ?>? ?<=? ?>=?` | BinaryOperation, then Convert to bool |

Plus `let`, `if`, sequencing, `for x in items do …`, `while … do …` and ordinary F# code. Loop bodies
reuse the block's call sites across iterations. As in `async { }`, a `let mutable` cannot be captured by a
loop body; use a `ref` or an object. `try`, `let rec` and `match` on active patterns are not translated
(`LeafExpressionConverter` limits).

Rules: the enclosing module, type or member must be `[<ReflectedDefinition>]` (a clear
`DlrTranslationException` says so otherwise); one `dlr { }` per source line (the body is located by
line inside the reflected definition); not inside generic functions or members yet.
Calling any of the operators or `Dlr.*` markers outside `dlr { }` throws `InvalidOperationException`.
`Dlr.named` and `Dlr.idx` exist only to give F# something it can type-check; `Named<'T>` and
`Indexed<'T>` have no constructors and are never instantiated.

## How it works

1. Without a `Quote` member, `dlr { … }` desugars to `dlr.Run(dlr.Delay(fun () -> …), file, line)`.
   `Delay` returns the closure unevaluated. Its compiler-generated type is unique to the block and
   its fields are the captured variables, named after them (`this` included; a captured
   `let mutable` is an `FSharpRef` cell).
2. On the first call `Discover` finds the block's body: it reads the `[<ReflectedDefinition>]`
   quotations of the closure's declaring type and nested types (FSharp.Core caches those once
   decoded) and picks the `Run` call whose baked `CallerLineNumber` matches.
3. `Translate` strips the builder calls, turns each free variable into a read of the closure field
   of that name, and replaces each dynamic operation by `site.Target.Invoke(site, …)` where `site`
   is a `CallSite<_>` embedded as a `Value` — which `LeafExpressionConverter` turns into
   `Expression.Constant`. Literals stay constants. The result is compiled to `Func<obj, 'T>` and
   cached in `DlrCache` under the closure type.

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
