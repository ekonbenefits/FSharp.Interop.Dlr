# Call sites

What each dynamic operation becomes, how its arguments are typed, and how the sites are laid
out in the compiled delegate. Part of [internals](internals.md).

Every dynamic operation becomes one `CallSite<TDelegate>` created at translation time and embedded
in the expression tree as an `Expression.Constant` — the same thing the C# compiler emits as a
static field per `dynamic` operation. The delegate type is `Func<CallSite, target, args…, result>`
(`Action` when the result is discarded), built with `Expression.GetDelegateType` so it also works
past `Func`'s arity. `Binders.siteCall` emits `site.Target.Invoke(site, target, args…)` as a
quotation `Call` node; the site's polymorphic rule cache does the rest at run time.

Past `Func`'s 17 type parameters (15 arguments and up) that delegate type is emitted at run time, and a
quotation must not name it: FSharp.Core's `Expr.Call` checks ask the type's assembly
`ReflectionOnly`, which Mono's browser runtime has not implemented. So a wide site's call is
emitted as a placeholder whose types are all plain — `WideSite.Invoke(site: CallSite,
delegateType: Type, args: obj[])` — and the LINQ `SiteHoister` in `Translate.fs`, which visits
every tree after conversion anyway, rewrites it into the typed `Expression.Invoke` on the site's
`Target`, unboxing each argument back to its parameter type. The same tree as a narrow site gets,
one step later, on every runtime, not only wasm; nothing changes for sites of up to 14 arguments. Every compiled tree goes
through the hoister, the per-key templates of a computed name included. The per-key delegates of a
computed name or a `Dlr.namedOf` shape use the same idea one level up: past `Func`'s arity they
are a `Func<obj[], obj>` over the parameters packed at the call and unpacked inside
(`lambdaOver` / `packArguments`).

A site with `ref` / `out` parameters (`Dlr.ref v`, `Dlr.out`, #131) has a delegate emitted at run
time at any arity (`Func` has no byref parameters), and a quotation cannot pass a byref anyway. Its
call is a placeholder too, `ByRefSite.Invoke<'H>(site, delegateType, args: obj[], byRefs, outs, sameAs) : 'H`
(`sameAs` marks a variable passed by ref twice, which shares one storage, as in C#),
whose `'H` holds the result then each byref argument's value after the call — a `ValueTuple<obj,
T1, …>` (nested in `Rest` past seven), so the values come back typed. The hoister rewrites it into
the typed `Invoke` over a LINQ variable per byref parameter (a ref's value in, the default for an
out — the out positions are explicit, since a per-key template's value in is a parameter and an
emitted delegate's parameter carries no `[Out]`), which LINQ writes back, and builds the holder
from them: no array and no boxing. The translator reads its fields into the result tuple and
assigns each ref back to its `let mutable` (or the ref cell a captured one becomes). [benchmarks.md](benchmarks.md)
has `d?TryGetValue(k, Dlr.out)` against C# `dynamic`'s `out int v`: a few nanoseconds more, and 48 B
against 24 B — the F# tuple beside the boxed result both pay; read into a struct tuple, only the box
(`Tests/HotPath.fs` pins both).
The placeholder's own body, `DynamicInvoke`, writes byrefs back on the JIT but not on Mono's
interpreter, so the rewrite is required there, not only faster. The argument flags are C#'s:
`IsOut` / `IsRef` with `UseCompileTimeType`, the out's type being its element of the block's result
type (C# needs a written type for a dynamic call's out, CS8197, for the same reason).

## Argument typing

Decided once per site (`Binders.Arg`):

| position | static type | `CSharpArgumentInfoFlags` |
| --- | --- | --- |
| target | `obj` | `None` — dispatch on the runtime type |
| argument with a known F# type | that type | `UseCompileTimeType` — C# overload rules on the static type, no boxing |
| argument typed `obj` | `obj` | `None` — dispatch on the runtime type |
| literal | its type | `UseCompileTimeType ||| Constant` — C#'s constant conversions |
| `Dlr.named` field | its type | `… ||| NamedArgument` with the field name |
| `Dlr.namedOf` value | `obj` | `NamedArgument` with the run-time name (see below) |

The binder context (accessibility) is the type declaring the member that contains the block —
what C# passes as the calling class; what that lets through is in [restrictions](restrictions.md)
and [binders](binders.md). Results come back as `obj` and go through a second,
`Convert` site to the inferred type (skipped for `obj`; a `unit` invocation uses a `ResultDiscarded`
void site, a `unit` read or operator just drops the value).

## Sites per operation

| Syntax | Binder(s) |
| --- | --- |
| `x?Name` | `GetMember` + `Convert` |
| `x?Name(a, b)` | `InvokeMember` (through `FSharpInvokeMemberBinder`, see [binders](binders.md)) + `Convert` |
| `x?Name <- v` | `SetMember` |
| `Dlr.addAssign` / `subtractAssign` | `IsEvent`, then either `InvokeMember add_Name` (`InvokeSpecialName`, discarded) or `GetMember` + `BinaryOperation AddAssign` + `SetMember` (`ValueFromCompoundAssignment`) — the C# compiler's shape for `+=` |
| `Dlr.call x (args)`, `x \|> Dlr.apply args` | `Invoke` (through `FSharpInvokeBinder`) + `Convert` |
| `Dlr.call x` typed `A -> B -> R` | `Invoke` site with typed argument slots + `Convert`, wrapped by `FunctionMember.CurriedN` / `TupledN` like a member read (past five, a function from a factory compiled once per site: `FunctionBuilder`); the target returned as it is when it already is a function of the type |
| `Dlr.Static<T>.Overloads?M(a)` | the member's usual `InvokeMember` site with `typeof<T>` as argument 0, flagged `UseCompileTimeType ||| IsStaticType` (C#'s shape for `T.M(dynamicArg)`); C#'s binder alone, the F#-aware wrappers look at instances. Only calls: C#'s `GetMember`/`SetMember`/`IsEvent` have no static form, so the other operations on it are a translation error |
| `Dlr.new'<T>(a, b)` | `InvokeConstructor` + `Convert`; `typeof<T>` is argument 0 of the site, flagged `UseCompileTimeType ||| IsStaticType`, the C# compiler's shape for `new T(dynamicArg)` |
| `x \|> Dlr.item i`, `x \|> Dlr.setItem i v` | `GetIndex` / `SetIndex` |
| `?+?` … | `BinaryOperation` + `Convert` |
| `Dlr.neg` … | `UnaryOperation` + `Convert` |
| `Dlr.cast<T>` | `Convert` with `ConvertExplicit` |
| `Dlr.implicit` | `Convert` |
| `x?Name` typed `A -> B -> R` | `InvokeMember` site with typed argument slots + `Convert`, wrapped in a curried F# function by `FunctionMember.CurriedN` / `TupledN` (past five, a function from a factory compiled once per site: `FunctionBuilder`); `unit -> R` uses `FSharpReadOrInvokeBinder` |
| `(?) x name`, variable name; `x?M(Dlr.typeArgsOf ts)`, variable list | see below |

## Computed names and runtime type arguments

When the member name, the type arguments, or both are only known at run time, the operation's
delegate is still compiled once, at translation time, with its `CallSite`s as parameters: the
shape does not depend on the name, only the sites do, so the sites are lifted out of a template
built for a placeholder key. A `SiteCache` constant keyed by `(name, types)` — whichever of the
two is static being a constant in the key; its bound and lifetime are in [caches](caches.md) —
creates the sites per distinct key, and the emitted code is

```
let sites = cache.Get((name, types)) in delegate.Invoke(sites.[0], …, target, args…)
```

A new key creates binders and sites (microseconds) — no `Compile()` — and then pays the DLR's
own first bind like any site. Argument names in `Dlr.named` stay static.

## Run-time argument names and counts

`Dlr.namedOf pairs` and `Dlr.argsOf values` change the site's *arity*, so per distinct argument
shape the whole operation is compiled, not only its sites: a `NamedOfCache` constant holds one
compiled delegate per shape — the ordered names, an empty name standing for a positional value
(`argsOf`'s first, then `namedOf`'s names) — taking the target, the fixed arguments and the
splatted values as one `obj[]`, all of one delegate type, so the call is a typed `Invoke`. The
compiled call keeps the source order of fixed arguments and the positional splat; named
arguments are the trailing ones (the binder's `CallInfo` names the last arguments), so a
positional after `namedOf` is a translation error. A lookup compares the pairs' names with the last
two shapes served (a site that repeats or alternates shapes hits there), else hashes them in
place for a dictionary, allocating nothing but the values array; a miss is a `Compile()` (once). At
`Capacity` (256, as `SiteCache`) entries it clears. The cost is the
`Dlr.namedOf` / `Dlr.argsOf` rows of [benchmarks](benchmarks.md), against `Dlr.named` and C#'s
named arguments. With a
computed member name or run-time type arguments, their
expressions are evaluated in the block's scope and passed into the per-name-list delegate as
parameters, where the operation is a `keyedSiteCore` of its own: a delegate per name list (few),
sites per member name inside it (many, no `Compile()`) — each cost where it belongs.

## Sites hoisted into locals

The call sites are `Expression.Constant`s. `LambdaExpression.Compile` keeps a reference-type
constant in the closure's `Constants` array and re-reads and casts it at each use, two per site
call (`site.Target` and the `site` argument), so a LINQ `ExpressionVisitor` (`SiteHoister` in
`Translate.fs`) gives each lambda — the block's own and every nested loop/try body — a `Block`
binding the sites its body uses to variables assigned once at entry; a use is a local read. Per
lambda, because a variable captured by a nested lambda would be a `StrongBox` read, no better than
the constant; on the LINQ tree, because FSharp.Core below 10.1 converts a quotation `Let` into a
nested lambda invocation, an order of magnitude slower (the floor is 10.1.201 for the same
converter's `Sequential`/`PropertySet` support). Hoisting takes a member call from well over C#
`dynamic`'s cost to a few nanoseconds above it; the rest is the block's entry
([pipeline](pipeline.md)), whose by-reference reader is wrapped around the hoisted lambda after
this pass, so the site locals sit inside the copy of the machine.

The alternative, a holder type with static fields per block (the C# compiler's shape,
marginally faster), was built and rejected: a non-collectible holder cannot reference argument types from a
collectible `AssemblyLoadContext` (a regression for plugin hosts), and holders leaked after
`DlrCache.clear()`; a per-block collectible assembly fixed both but meant an assembly per block
in tooling. Hoisting gives up the nanosecond for none of that.
