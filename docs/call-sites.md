# Call sites

What each dynamic operation becomes, how its arguments are typed, and how the sites are laid
out in the compiled delegate. Part of [internals](internals.md).

Every dynamic operation becomes one `CallSite<TDelegate>`, created at translation time and
embedded in the expression tree as an `Expression.Constant`. That is the same thing the C#
compiler emits as a static field per `dynamic` operation.

- The delegate type is `Func<CallSite, target, args…, result>` (`Action` when the result is
  discarded), built with `Expression.GetDelegateType` so it also works past `Func`'s arity.
- `Binders.siteCall` emits `site.Target.Invoke(site, target, args…)` as a quotation `Call` node.
- The site's polymorphic rule cache does the rest at run time.

## Wide sites

Past `Func`'s 17 type parameters (15 arguments and up), the site's delegate type is emitted at
run time, and a quotation must not name it: FSharp.Core's `Expr.Call` checks ask the type's
assembly `ReflectionOnly`, which Mono's browser runtime has not implemented.

So a wide site's call is emitted as a placeholder whose types are all plain,
`WideSite.Invoke(site: CallSite, delegateType: Type, args: obj[])`. The LINQ `SiteHoister` in
`SiteHoister.fs`, which visits every tree after conversion anyway, rewrites it into the typed
`Expression.Invoke` on the site's `Target`, unboxing each argument back to its parameter type.

- The result is the same tree a narrow site gets, one step later, on every runtime, not only
  wasm.
- Nothing changes for sites of up to 14 arguments.
- Every compiled tree goes through the hoister, the per-key templates of a computed name
  included.

The per-key delegates of a computed name or a `Dlr.namedOf` shape use the same idea one level
up: past `Func`'s arity they are a `Func<obj[], obj>` over the parameters packed at the call and
unpacked inside (`lambdaOver` / `packArguments`).

## Byref sites

A site with `ref` / `out` parameters (`Dlr.ref v`, `Dlr.out`, #131) has a delegate emitted at run
time at any arity (`Func` has no byref parameters), and a quotation cannot pass a byref anyway.
Its call is a placeholder too:

```
ByRefSite.Invoke<'H>(site, delegateType, args: obj[], byRefs, outs, sameAs) : 'H
```

- `sameAs` marks a variable passed by ref twice, which shares one storage, as in C#.
- `'H` holds the result, then each byref argument's value after the call: a
  `ValueTuple<obj, T1, …>` (nested in `Rest` past seven), so the values come back typed.

The hoister rewrites it into the typed `Invoke` over a LINQ variable per byref parameter, which
LINQ writes back, and builds the holder from them: no array and no boxing.

- A ref's variable starts with its value; an out's with the default.
- The out positions are passed explicitly: a per-key template's value comes in as a parameter,
  and an emitted delegate's parameter carries no `[Out]`.
- The translator reads the holder's fields into the result tuple and assigns each ref back to
  its `let mutable` (or the ref cell a captured one becomes).

The rewrite is required on wasm, not only faster: the placeholder's own body, `DynamicInvoke`,
writes byrefs back on the JIT but not on Mono's interpreter.

The argument flags are C#'s: `IsOut` / `IsRef` with `UseCompileTimeType`, the out's type being
its element of the block's result type. (C# needs a written type for a dynamic call's out,
CS8197, for the same reason.)

Cost: `d?TryGetValue(k, Dlr.out)` is a few nanoseconds more than C# `dynamic`'s `out int v`
(the `out` row of [benchmarks.md](benchmarks.md#the-same-operation-each-way)). It allocates
48 B against C#'s 24 B:

- Both pay 24 B to box the method's result, since a dynamic call returns `object`.
- The other 24 B is the F# tuple that carries the result and the out back
  (`let found, v = …`).
- Read into a struct tuple instead (`let struct (found, v) = …`) and that tuple is not
  allocated: 24 B, the same as C#.

`Tests/HotPath.fs` pins both figures.

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
| `x?Name(a, b)` | `InvokeMember` (through `FSharpInvokeMemberBinder` for a positional call without type arguments, see [binders](binders.md); a named or generic call is C#'s binder as is) + `Convert` |
| `x?Name <- v` | `SetMember` (through `FSharpSetMemberBinder`: the value conversions, [binders](binders.md)) |
| `Dlr.addAssign` / `subtractAssign` | `IsEvent`, then either `InvokeMember add_Name` (`InvokeSpecialName`, discarded) or `GetMember` + `BinaryOperation AddAssign` + `SetMember` (`ValueFromCompoundAssignment`) — the C# compiler's shape for `+=` |
| `Dlr.call x (args)`, `x \|> Dlr.apply args` | `Invoke` (through `FSharpInvokeBinder` for a positional call) + `Convert` |
| `Dlr.call x` typed `A -> B -> R` | `Invoke` site with typed argument slots + `Convert`, wrapped by `FunctionMember.CurriedN` / `TupledN` like a member read (past five, a function from a factory compiled once per function type and site type, so a computed name's per-key sites share it: `FunctionBuilder`); no `Convert` for a `… -> unit` result (a void site); the target returned as it is when it already is a function of the type |
| `Dlr.Static<T>.Overloads?M(a)` | the member's usual `InvokeMember` site with `typeof<T>` as argument 0, flagged `UseCompileTimeType ||| IsStaticType` (C#'s shape for `T.M(dynamicArg)`), through `FSharpStaticInvokeMemberBinder` for a positional non-generic call: the argument rules (F# optional parameters, function ↔ delegate conversions) apply to `T`'s static methods as C#'s error suggestion; the function-*member* rules need an instance and do not. Only calls: C#'s `GetMember`/`SetMember`/`IsEvent` have no static form, so the other operations on it are a translation error |
| `Dlr.new'<T>(a, b)` | `InvokeConstructor` (through `FSharpInvokeConstructorBinder` for a positional call), no `Convert`: the site itself is typed `T`, as the C# compiler's is (an `obj`-typed site would reject a struct result); `typeof<T>` is argument 0 of the site, flagged `UseCompileTimeType ||| IsStaticType`, the C# compiler's shape for `new T(dynamicArg)` |
| `x \|> Dlr.item i`, `x \|> Dlr.setItem i v` | `GetIndex` (C#'s binder as is) / `SetIndex` (through `FSharpSetIndexBinder`) |
| `?+?` … | `BinaryOperation` + `Convert` |
| `Dlr.neg` … | `UnaryOperation` + `Convert` |
| `Dlr.cast<T>` | `Convert` with `ConvertExplicit` |
| `Dlr.implicit` | `Convert` |
| `x?Name` typed as a delegate type | `GetMember` through `FSharpGetMemberOrMethodBinder` + `Convert`: C#'s rule for a property, field or dynamic object's member; where C# fails because the name is a method, the `MethodGroup` marker as its error suggestion, which the block answers with the `x?Name` read as a function type below (curried up to five parameters, tupled past them) converted to the delegate by `FunctionConversions` |
| `x?Name` typed `A -> B -> R` | `InvokeMember` site with typed argument slots + `Convert`, wrapped in a curried F# function by `FunctionMember.CurriedN` / `TupledN` (past five, a function from a factory compiled once per function type and site type: `FunctionBuilder`); no `Convert` for a `… -> unit` result; `unit -> R` uses `FSharpReadOrInvokeBinder` |
| `(?) x name`, variable name; `x?M(Dlr.typeArgsOf ts)`, variable list | see below |

`MetaObjectAwareBinder` also wraps the InvokeMember, Invoke, SetMember, SetIndex and compound-assignment sites, so an F# function handed to a dynamic object arrives as a delegate; the six comparison operators go through `FSharpBinaryOperationBinder` ([binders](binders.md)).

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
`SiteHoister.fs`) gives each lambda — the block's own and every nested loop/try body — a `Block`
binding the sites its body uses to variables assigned once at entry; a use is a local read. Per
lambda, because a variable captured by a nested lambda would be a `StrongBox` read, no better than
the constant; on the LINQ tree, because FSharp.Core below 10.1 converts a quotation `Let` into a
nested lambda invocation, an order of magnitude slower (the floor is 10.1.201 for the same
converter's `Sequential`/`PropertySet` support). Hoisting takes a member call from well over C#
`dynamic`'s cost to a few nanoseconds above it; the rest is the block's entry
([pipeline](pipeline.md)), whose by-reference reader is wrapped around the hoisted lambda after
this pass, so the site locals sit inside the copy of the machine.

A member call, with its sites hoisted ([all the examples](trees.md)):

<!-- tree:call -->
```fsharp
[<ReflectedDefinition>]
let call (o: obj) (x: int) : int = dlr { return o?Add(x, 1) }
```

```csharp
sm =>
{
    // Convert to Int32 (implicit): C#'s binder
    var convertInt32 = <constant CallSite<Func<CallSite, object, int>>>;
    // InvokeMember Add, 2 arguments: FSharpInvokeMemberBinder (C#'s, plus the F# rules), meta-object aware
    var invokeAdd = <constant CallSite<Func<CallSite, object, int, int, object>>>;

    return convertInt32.Target.Invoke(convertInt32, invokeAdd.Target.Invoke(
        invokeAdd,
        sm.o,
        sm.x,
        1));
}
```
<!-- /tree:call -->

The alternative, a holder type with static fields per block (the C# compiler's shape,
marginally faster), was built and rejected: a non-collectible holder cannot reference argument types from a
collectible `AssemblyLoadContext` (a regression for plugin hosts), and holders leaked after
`DlrCache.clear()`; a per-block collectible assembly fixed both but meant an assembly per block
in tooling. Hoisting gives up the nanosecond for none of that.
