# FSharp.Interop.Dlr

Experimental. A `dlr { }` computation expression in which the `?` operator (and friends) is
never executed: the body is captured as a quotation, translated once into a LINQ expression
tree whose Microsoft.CSharp `CallSite`s are baked in as constants, compiled to a delegate that
takes the closure values as an argument, and cached by the source file and line of the block.

```fsharp
open FSharp.Interop.Dlr

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

Plus `let`, `if`, sequencing and ordinary F# code, via `LeafExpressionConverter`.

Rules: one `dlr { }` per source line (the cache key is file + line; a mismatch throws).
Calling any of the operators or `Dlr.*` markers outside `dlr { }` throws `InvalidOperationException`.
`Dlr.named` and `Dlr.idx` exist only to give F# something it can type-check; `Named<'T>` and
`Indexed<'T>` have no constructors and are never instantiated.

## How it works

1. `DlrBuilder.Quote`/`Run` receive the body as `Expr<'T>` plus `[<CallerFilePath>]` and
   `[<CallerLineNumber>]`.
2. `Translate` strips the builder calls, turns every `Value` node into a read from a `obj[]`
   slot (same pre-order walk used per call to extract the values), and replaces each dynamic
   operation by `site.Target.Invoke(site, ...)` where `site` is a `CallSite<_>` embedded as a
   `Value` — which `LeafExpressionConverter` turns into `Expression.Constant`.
3. The `Func<obj[], 'T>` is compiled and cached in `DlrCache` under `(file, line)`.

## Measured (Release, net10.0, Apple Silicon, 1M-call average)

| | ns/call |
| --- | --- |
| compiled delegate alone (`Func<obj[], int>` with baked call sites) | 16 |
| extracting closure slots from the quotation + delegate | 460 |
| `dlr { return w?Add(i, 1) }` end to end | ~10 000 |
| FSharp.Interop.Dynamic `w?Add(i, 1)` | ~7 900 |

The end-to-end cost is dominated by the F# compiler rebuilding the quotation on every
evaluation of the block (`Expr.Deserialize40`, 7–10 µs); it does not cache quotations, even
ones without free variables. So the compiled form is fast, but reaching it through a quoted
computation expression on every call is not.
