# Call sites

What each dynamic operation becomes, how its arguments are typed, and how the sites are laid
out in the compiled delegate. Part of [internals](internals.md).

Every dynamic operation becomes one `CallSite<TDelegate>` created at translation time and embedded
in the expression tree as an `Expression.Constant` — the same thing the C# compiler emits as a
static field per `dynamic` operation. The delegate type is `Func<CallSite, target, args…, result>`
(`Action` when the result is discarded), built with `Expression.GetDelegateType` so it also works
past `Func`'s arity. `Binders.siteCall` emits `site.Target.Invoke(site, target, args…)` as a
quotation `Call` node; the site's polymorphic rule cache does the rest at run time.

## Argument typing

Decided once per site (`Binders.Arg`):

| position | static type | `CSharpArgumentInfoFlags` |
| --- | --- | --- |
| target | `obj` | `None` — dispatch on the runtime type |
| argument with a known F# type | that type | `UseCompileTimeType` — C# overload rules on the static type, no boxing |
| argument typed `obj` | `obj` | `None` — dispatch on the runtime type |
| literal | its type | `UseCompileTimeType ||| Constant` — C#'s constant conversions |
| `Dlr.named` field | its type | `… ||| NamedArgument` with the field name |

The binder context (accessibility) is the type declaring the member that contains the block —
what C# passes as the calling class. Results come back as `obj` and go through a second,
`Convert` site to the inferred type (skipped for `obj`; a `unit` invocation uses a `ResultDiscarded`
void site, a `unit` read or operator just drops the value).

## Sites per operation

| Syntax | Binder(s) |
| --- | --- |
| `x?Name` | `GetMember` + `Convert` |
| `x?Name(a, b)` | `InvokeMember` (through `FSharpInvokeMemberBinder`, see [binders](binders.md)) + `Convert` |
| `x?Name <- v` | `SetMember` |
| `Dlr.addAssign` / `subtractAssign` | `IsEvent`, then either `InvokeMember add_Name` (`InvokeSpecialName`, discarded) or `GetMember` + `BinaryOperation AddAssign` + `SetMember` (`ValueFromCompoundAssignment`) — the C# compiler's shape for `+=` |
| `Dlr.call args x` | `Invoke` (through `FSharpInvokeBinder`) + `Convert` |
| `Dlr.Static<T>.Overloads?M(a)` | the member's usual `InvokeMember` site with `typeof<T>` as argument 0, flagged `UseCompileTimeType ||| IsStaticType` (C#'s shape for `T.M(dynamicArg)`); C#'s binder alone, the F#-aware wrappers look at instances. Only calls: C#'s `GetMember`/`SetMember`/`IsEvent` have no static form, so the other operations on it are a translation error |
| `Dlr.new'<T>(a, b)` | `InvokeConstructor` + `Convert`; `typeof<T>` is argument 0 of the site, flagged `UseCompileTimeType ||| IsStaticType`, the C# compiler's shape for `new T(dynamicArg)` |
| `x \|> Dlr.item i`, `x \|> Dlr.setItem i v` | `GetIndex` / `SetIndex` |
| `?+?` … | `BinaryOperation` + `Convert` |
| `Dlr.neg` … | `UnaryOperation` + `Convert` |
| `Dlr.cast<T>` | `Convert` with `ConvertExplicit` |
| `Dlr.implicit` | `Convert` |
| `x?Name` typed `A -> B -> R` | `InvokeMember` site with typed argument slots + `Convert`, wrapped in a curried F# function by `FunctionMember.CurriedN` / `TupledN`; `unit -> R` uses `FSharpReadOrInvokeBinder` |
| `(?) x name`, variable name; `x?M(Dlr.typeArgsOf ts)`, variable list | see below |

## Computed names and runtime type arguments

When the member name, the type arguments, or both are only known at run time, the operation's
delegate is still compiled once, at translation time, with its `CallSite`s as parameters: the
shape does not depend on the name, only the sites do, so the sites are lifted out of a template
built for a placeholder key. A `SiteCache` constant keyed by `(name, types)` — whichever of the
two is static being a constant in the key — creates the sites per distinct key, and the emitted
code is

```
let sites = cache.Get((name, types)) in delegate.Invoke(sites.[0], …, target, args…)
```

A new key creates binders and sites (microseconds) — no `Compile()` — and then pays the DLR's
own first bind like any site. Argument names in `Dlr.named` stay static.

## Sites hoisted into locals

The call sites are `Expression.Constant`s. `LambdaExpression.Compile` keeps a reference-type
constant in the closure's `Constants` array and re-reads and casts it at each use, two per site
call (`site.Target` and the `site` argument), so a LINQ `ExpressionVisitor` (`SiteHoister` in
`Translate.fs`) gives each lambda — the block's own and every nested loop/try body — a `Block`
binding the sites its body uses to variables assigned once at entry; a use is a local read. Per
lambda, because a variable captured by a nested lambda would be a `StrongBox` read, no better than
the constant; on the LINQ tree, because FSharp.Core before 10.1 converted a quotation `Let` into a
nested lambda invocation (measured 50× slower; the floor is now 10.1, for the same converter's
`Sequential`/`PropertySet` support). Measured, a member call went from ~30 ns to ~18 against C#
`dynamic`'s ~7.5; what remains is the block's entry ([pipeline](pipeline.md)).

An earlier design emitted a holder type with static fields per block (the C# compiler's shape,
~1 ns faster) and was dropped: a non-collectible holder cannot reference argument types from a
collectible `AssemblyLoadContext` (a regression for plugin hosts), and holders leaked after
`DlrCache.clear()`; a per-block collectible assembly fixed both but meant an assembly per block
in tooling. Hoisting gives up the nanosecond for none of that.
