# Internals: binders, call sites and caches

What a `dlr { }` block turns into at run time, and where every piece of state lives. The README
covers usage; this is for reading or changing the library.

## The pipeline

```
dlr { … }                      F# desugars to  dlr.Run(dlr.Delay(fun () -> …), file, line)
  │
  ▼  Run                       key: the closure's compiler-generated type
DlrCache ──miss──▶ Discover ──▶ Translate ──▶ LeafExpressionConverter ──▶ Func<obj, 'T>
  │                (body from     (quotation → quotation with sites baked in)
  │ hit             ReflectedDefinition)
  ▼
compiled.Invoke(closure)       ~18 ns: field reads + one CallSite per operation
```

`Delay` returns the closure unevaluated. `Run` never executes it; it is the call site's identity
(its type) and the source of the captured values (its fields).

`dlrq { … }` has a `Quote` member, so F# desugars it to `dlrq.Run(<@ dlrq.Delay(fun () -> …) @>,
file, line)` and builds that quotation literal on every call (`Deserialize40`, ~7 µs for a small
block, uncached by FSharp.Core). The captured values are inside it as `ValueWithName` nodes.
`Quoted.scan` walks it per call, reading those values into an `obj[]` and recording the shape
(every type and member the quotation mentions, every literal, and each bound variable as its
index in scope); `QuotedSites<'T>` looks the shape up under the file and line and invokes the
`Func<obj[], 'T>`. Misses are admitted under a lock, so concurrent first calls compile once. On a miss `Quoted.prepare` replaces each `ValueWithName` with a
read of its slot, in the same traversal order, and the same `Translate.translate` compiles the
result. No enclosing member is known, so the binder context is `obj`. See the caches table and
"Translation notes".

## Caches

| Cache | Key | Value | Lifetime | Where |
| --- | --- | --- | --- | --- |
| `DlrCache` | closure `Type` (one per block; per instantiation for generic members) | `Compiled { Delegate: Func<obj,'T>; ResultType }` | process; `DlrCache.clear()` drops it | `Cache.fs` |
| `Sites<'T>` | closure `Type`, per result type | the same delegate, already typed `Func<obj,'T>`, stamped with the clear generation it was compiled under — the hot path's lookup, no cast; plus a last-compiled slot (an immutable entry swapped atomically) so a block called repeatedly pays a reference compare, not a hash lookup, and blocks called in turn pay a lookup each and never write | process; `DlrCache.clear()` bumps the generation, so no pre-clear entry is served however it got installed | `Cache.fs` |
| `QuotedSites<'T>` | `file * line`, per result type; within a site, the quotation's shape (so a generic enclosing function's instantiations each get an entry; at most 8 per site) | `Func<obj[],'T>` over the slot array, generation-stamped like `Sites<'T>` | process; `DlrCache.clear()` bumps the generation and clears it; counted in `DlrCache.count()` | `Cache.fs` |
| reflected definitions | declaring `Type` (module or class) | every `(MethodBase, Expr)` with a reflected definition on it and its nested types | process | `Discover.fs` |
| `SiteCache<'Key>` | `string * Type list` — the member name and the explicit type arguments; whichever is static is a constant in the key | the operation's `CallSite[]` for that key | per site (a constant in the compiled tree); at `Capacity` (256) entries it clears and refills | `Binders.fs`, for `(?) x name` with a variable name |
| DLR rule cache | runtime types (restrictions) | the bound rule | per `CallSite<_>` | inside each site, owned by the DLR |

`DlrCache` and the reflected-definition cache are process-wide, as are the two conversion caches
in `Binders.fs` (`FunctionConversions.conversions`, `DelegateConversions.makers`: one entry per
(function type, delegate type) pair, bounded by the program's types) and `SiteCache.Capacity`.
The rest is baked into a block's compiled delegate, so it is collected with it. The call sites
are `Expression.Constant`s, hoisted: `LambdaExpression.Compile` keeps a reference-type constant
in the closure's `Constants` array and re-reads and casts it at each use, two per site call, so a
LINQ `ExpressionVisitor` (`SiteHoister` in `Translate.fs`) gives each lambda — the block's own and
every nested loop/try body — a `Block` binding the sites its body uses to variables assigned once
at entry; a use is a local read. Per lambda, because a variable captured by a nested lambda would
be a `StrongBox` read, no better than the constant; on the LINQ tree, because FSharp.Core before
10.1 converts a quotation `Let` into a nested lambda invocation (measured 50× slower — the floor
is 6.0.1). Measured, a member call went from ~30 ns to ~18 against C# `dynamic`'s ~7.5; what
remains is the block's entry: the `Delay` closure F# allocates (3 ns), `GetType()` on it (3), the
last-hit compare and the delegate invoke.

## Call sites

Every dynamic operation becomes one `CallSite<TDelegate>` created at translation time and embedded
in the expression tree as an `Expression.Constant` — the same thing the C# compiler emits as a
static field per `dynamic` operation. The delegate type is `Func<CallSite, target, args…, result>`
(`Action` when the result is discarded), built with `Expression.GetDelegateType` so it also works
past `Func`'s arity. `Binders.siteCall` emits `site.Target.Invoke(site, target, args…)` as a
quotation `Call` node; the site's polymorphic rule cache does the rest at run time.

Argument typing, decided once per site (`Binders.Arg`):

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

### Sites per operation

| Syntax | Binder(s) |
| --- | --- |
| `x?Name` | `GetMember` + `Convert` |
| `x?Name(a, b)` | `InvokeMember` (through `FSharpInvokeMemberBinder`, below) + `Convert` |
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
| `(?) x name`, variable name; `x?M(Dlr.typeArgsOf ts)`, variable list | the operation's delegate compiled once at translation time with its `CallSite`s as parameters (the shape does not depend on the name; the sites are lifted out of a template built for a placeholder), plus a `SiteCache` constant keyed by `(name, types)`, whichever of the two is static being a constant in the key: `let sites = cache.Get((name, types)) in delegate.Invoke(sites.[0], …, target, args…)`. The operation's shape depends on neither input, only its sites do. A new name creates binders and sites (µs) — no `Compile()` — and then pays the DLR's own first bind like any site |

## The F#-aware binders

The C# binder invokes delegates and dynamic objects; an F# function value is an `FSharpFunc`
object, which it reports as "Cannot invoke a non-delegate type". Three binders in `Binders.fs`
subclass the DLR's binder types, wrap C#'s, and add the F# case in the fallbacks — as rules, not
exceptions, so the decision is cached per runtime type like everything else:

- **`FSharpInvokeMemberBinder`** (`x?Name(args)`). `FallbackInvokeMember` (a CLR target): if the
  type has an accessible property or field of that name whose type is a fitting `FSharpFunc`
  shape and no method of that name, the rule reads and applies it; otherwise C#'s binding — with a rule for a method whose F#
  optional parameters (`?arg`, i.e. `[<OptionalArgument>] FSharpOption<'T>`) the arguments fit
  once omitted ones are `None` and bare values `Some`, offered as the *error suggestion*, which
  C# uses only where its own binding fails (`OptionalArguments.tryCall`). `FallbackInvoke` (a dynamic target has
  produced the member's value): delegates to `FSharpInvokeBinder`.
- **`FSharpInvokeBinder`** (`Dlr.call`, and the value step above). `FallbackInvoke`: if the value's
  runtime type is a candidate, a rule applying it restricted to that type; otherwise C#'s `Invoke`.
  A value-less meta-object (Expando's `BindInvokeMember` hands over the member before evaluating
  it) is `Defer`red, so the nested site binds through this binder with the value.
- **`FSharpReadOrInvokeBinder`** (`x?Name` read as `unit -> R`). A parameterless method is
  invoked; a property or field is read (and applied if it is an F# function); on a dynamic
  target the value itself is the result unless it is a delegate or function.

The shape is read off the *function's* type — the runtime type of a dynamic value, the declared
type of a CLR member — never off the call's declared result: `FSharpFunc<A * B, R>` (tupled) or
`FSharpFunc<A, FSharpFunc<B, R>>` (curried) whose domains the argument types fit. The rule is
built for any arity: a tuple construction and one `Invoke` for tupled, a chain of `Invoke` calls
for curried (each step's result is the next function; `OptimizedClosures` override `Invoke`
too, so the chain is correct, just not `InvokeFast`), the result boxed for the site's `Convert`. So a discarded call or a widened result still applies the function that is
there. A CLR member whose declared type says nothing (`obj`, an interface) is read and handed to a
nested `Invoke` site that decides by the value's runtime type. Reading a member *as* a function
(`FunctionMember.CurriedN`/`TupledN`) constructs an F# closure and so has per-arity helpers up to
five like `OptimizedClosures`; a curried read past five builds a chain of `CurryStep` closures at
run time that collects the arguments and invokes the site's delegate once (`DynamicInvoke`, so
slower than the typed helpers), tupled reads past five are a translation error. The argument types a shape has to fit are the meta-objects' `LimitType`s — the runtime type of an `obj`-typed argument, the static
type of a typed one — matching the site's own argument rules. Our reflection lookups (function
members, optional-parameter methods) apply the C# binder's accessibility rule from the same
context type: public always, internal from the same assembly (F# `private` is IL internal),
private from inside the declaring type. Named or generic calls use C#'s binder unchanged. A member read as `… -> unit` is invoked through a void, result-discarded site.

The reflection fallback is one `tryInvoke` over candidates — instance methods (`tryCall`), the
static methods of `Dlr.Static<T>.Overloads` (`tryStaticCall`, through
`FSharpStaticInvokeMemberBinder`), constructors (`tryConstruct`, through
`FSharpInvokeConstructorBinder`: C#'s constructor binder takes no error suggestion, its failure
is a rule that throws, so ours applies exactly when C#'s bind is that throw) and a delegate
target's `Invoke` (`tryInvokeDelegate`, from `FSharpInvokeBinder`; a delegate-typed member is
routed to that nested site). `FunctionShapes.applyCall` takes the same conversions through a hook
(`convertArgument`, set once `OptionalArguments` exists), so an F# function member whose domain is
a delegate or a function accepts the other kind too. The fallback (`OptionalArguments.tryCall`, C#'s error suggestion) converts an F#
function argument for a delegate parameter and a delegate argument for a function parameter.
Function to delegate: a `FunctionAdapters` instance (`Adapters.fs`, generated by
`generate-adapters.fsx`) whose `Invoke` has the delegate's exact
signature (curried/tupled × result/void, 0–16 parameters; `OptimizedClosures` for curried up to
five), built per call by a factory emitted once per (function type, delegate type) as IL —
`new Adapter(f)` and the delegate constructor over `Invoke`, ~30 ns, where
`Delegate.CreateDelegate` per call is ~300 ns and a LINQ closure about the same; past sixteen
parameters (a custom delegate type), a compiled lambda applying the function. Delegate to
function: a typed `DelegateFunctions` wrapper (`FSharpFunc` subclass calling the delegate's
`Invoke`; `OptimizedClosures` for curried, so `f a b` is one call) over the delegate rebound to
the `Func`/`Action` of its signature, again constructed by emitted IL; past five parameters
`TupledDelegateFunction`/`CurryStep` with `DynamicInvoke`. Not `FuncConvert`, whose wrapper loses
arguments on Mono's browser-wasm runtime. A related wasm fault, a nested non-capturing lambda
losing its arguments, is why every lambda and delegate literal written in a block is made to
capture the closure parameter there (`capturing` in `Translate.fs`, a no-op elsewhere). Measured
(Release, Apple Silicon): a bound call with a converted F# function argument ~140 ns, with a
converted delegate ~185 ns, against ~40 ns for an argument needing no conversion. Calls of a
curried F# function member go through `InvokeFast` (one call, no intermediate closures), ~38 ns.
A parameter typed `Delegate` itself (WinForms `Control.Invoke`) gets the
`Func`/`Action` F# would build for the function, and this rule goes *before* C#'s: left to C#,
`FSharpFunc`'s own `op_Implicit` yields a `Converter<Unit, R>` for a `unit -> R`, a one-parameter
delegate that a `DynamicInvoke()` then rejects.

`FSharpBinaryOperationBinder` wraps C#'s for the six comparison operators. C# first when either
operand is a type it covers — primitive, enum, decimal, delegate, string and bool for `==`/`!=` only (C# has no
ordering for them), a dynamic object (whose own rule reaches us as C#'s error suggestion) — or declares the CLR
operator (`op_Equality` and friends, including inherited); otherwise, and this is the one place
our rule goes *before* C#, because C# would silently bind reference equality for a record, the
rule is `LanguagePrimitives.GenericEquality`/`GenericComparison` on the boxed operands, restricted
on both runtime types (instance-restricted for a null). Non-comparable types fail the way F#'s
`compare` fails, with an `ArgumentException`. Arithmetic and bitwise operators are C#'s alone.

## Translation notes

`Translate.translate` compiles a body over one parameter — the Delay closure (`obj`) for
`dlr { }`, the slot array (`obj[]`) for `dlrq { }` — and knows both builder types, so a block of
either kind nested in one of the other compiles into it (a nested `dlrq` is a `QuoteTyped` node
around the same `Delay`). It is a dispatcher over three private sections — `Plumbing` (the builder's
methods), `Members` (the marker operations, `keyedSite`, `compoundAssign`) and `Captures`
(where a free variable comes from) — with the generic rewriting (`let mutable`, `let rec`,
lambdas) inline; each section takes the recursive rewriter as a parameter.

- **Normalisation** first (`Translate.normalize`): `|>` / `<|` and applications of the curried
  markers are beta-reduced (a parameter used once is substituted, otherwise `let`-bound), and
  `let`s of literals and variables are inlined, so `w |> Dlr.get "A"` is the same node as
  `Dlr.get "A" w` with a literal name.
- **Captured variables** become reads of the closure's fields by name; a `let mutable` is an
  `FSharpRef` field, read through `.Value`. When the Release optimizer inlined a value instead of
  capturing it, its definition is taken from the enclosing member's reflected body: a `let`, or
  the single application of a once-called local function.
- **Control flow** the expression converter has no node for (`for`, `while`, `try`, `use`) is
  emitted as calls to `DlrRuntime.*` helpers with the bodies as `Func` delegates (not F#
  lambdas: on browser-wasm the `FuncConvert` wrapper the converter would add lost arguments).
  `let rec` is tied through reference cells, and so is a `let mutable` of the block: loop and
  `try` bodies are compiled into delegates, and a tree variable cannot be assigned from inside
  one. A captured mutable already is a cell, so `v <- x` writes its `Value`.
- **Nested blocks** compile into the outer block: at run time their closure would be created by
  the compiled tree, not the compiler, and would have no reflected body.
- **`unit` bodies** end with the unit constant, since an F# `unit` call is `void` in IL and the
  converter will not return that.

## What the compiler is assumed to do

Specified F#: the computation-expression desugaring, caller-info arguments, `[<ReflectedDefinition>]`,
`LeafExpressionConverter`. Not specified — read by `Translate.Captures` and `Discover`:

- closure fields named after the captured variables, `this` as `this`, `FSharpRef` for mutables;
- closures nested in the enclosing module type, or in the file's `<StartupCode$…>` class for
  members of types declared in a namespace (hence the assembly-wide fallback in `Discover`);
- generic members: a closure class generic over the member's type parameters, under the same
  names (`Discover.instantiate` rebuilds the member with the closure's arguments);
- the Release optimizer inlining constants and once-called local functions.

A change in any of these raises `DlrTranslationException` on the first call at a site; nothing
binds silently wrong. CI builds with the .NET 8, 9 and 10 SDKs in Debug and Release.

## Measured

Release, net10.0, Apple Silicon; the current numbers for every path are in
[benchmarks.md](benchmarks.md) (`Benchmarks/bench.sh docs`). Older spot measurements, for the
function-member paths:

| | ns |
| --- | --- |
| block, `w?Add(i, 1)` on a method | 18 (was 29 before the hoisted sites and typed cache) |
| block, `e?Fn(i)` with `Fn` an F# function property | 33 |
| one site alternating between the two kinds | 70 |
| bound `int -> int -> int`, full application | 11 |
| bound `unit -> int` property read | 8.5 |
| static `w.Add(i, 1)` | 11–15 |
