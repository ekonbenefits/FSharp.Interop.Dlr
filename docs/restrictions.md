# The same restrictions as C# `dynamic`, and where it goes beyond

## Restrictions

Same binder, same limits. Each is pinned by a test ([where](#where-the-tests-are)).

### Finding members

- **Extension methods** are not found: the binder sees only the target's own members. Call the
  extension class statically instead: `Dlr.Static<Ext>.Overloads?M(x)`, or `Ext.M x` in plain F#.
- **Static members** cannot be reached through an instance. Static *calls* have their own target,
  `Dlr.Static<T>.Overloads`.
- **Explicitly implemented interface members** are not found. The binder sees the runtime type's
  public members, and an explicit implementation is a private method named `IFoo.Bar`.
  - In F# every interface implementation is explicit, so `o?Dispose()` on an F# `IDisposable`
    fails unless the type also exposes the member.
  - Cast to the interface statically (`o :?> IFoo`) and call it there.
- **Accessibility is C#'s, from the calling type.**
  - `private` binds only inside the declaring type; `internal` anywhere in the assembly;
    `protected` from a derived type, through a receiver of that type (C#'s qualifier rule).
  - F# `private` compiles to IL `internal`.
  - The declaring type counts too. A public member of a type the calling type cannot see is not
    found: another assembly's `internal` class, an anonymous type, a public generic over such a
    type. That holds through the library's own rules as through C#'s binder.
  - `[InternalsVisibleTo]` opens it, as for C#.

### Calling

- **No compile-time checking**: a misspelt member or wrong arity is a `RuntimeBinderException`
  at the call.
- **Generic type arguments** must be inferable from the arguments, or given explicitly:
  `Dlr.typeArgs<A, B>()`, or `Dlr.typeArgsOf [ … ]`, whose list may even be a run-time value.
- **`inline` members with a member constraint** (`^T: (member Name: string)`) are found but
  throw `NotSupportedException` when called: their body only exists at inlining sites. Operator
  constraints (`v + v`) are fine; they resolve at run time.

### Values

- **Target and result are `obj`.** A typed target is upcast, and the result converts to the
  inferred type, so value types box there; arguments do not. Two consequences C# `dynamic` users
  know:
  - a **struct target is a boxed copy**, so a mutating call through the box leaves the variable
    untouched;
  - a **`Nullable<T>` target erases**: the box holds a `T` or is `null`, so there is no
    `HasValue` or `Value` to find (the value reads as `T`).
- **Byrefs.** An `out` / `ref` parameter is `Dlr.out` / `Dlr.ref v` ([syntax](syntax.md)). A
  `byref` / `inref` / `outref` *value* cannot cross a dynamic operation. An `int[]` does reach a
  `Span<int>` parameter, through the implicit conversion, as in C#.

### Library-specific

Not C# restrictions, but the same family:

- **A block cannot live in an `inline` function or member.** In Release the function is expanded
  into every caller, taking the block's values with it and leaving its body behind. A Debug build
  calls it as a method, so it only appears to work. The analyzer reports it (`DLR004`).
- **Three markers take one shape only.** Any other use is a translation error, which the
  analyzer reports at build time.
  - `Dlr.named` takes the record literal itself: the names are read from the quotation, so a
    record held in a variable does not work. For names from data, use `Dlr.namedOf`.
  - `Dlr.Static<T>.Overloads` is a call target only.
  - `Dlr.call x` is read at a function type or applied.

### Platform

- **No NativeAOT, no trimming.** The runtime binder, `LambdaExpression.Compile()`, and the
  reflection that finds bodies and the captured variables' fields all need a JIT. The assembly is
  marked `IsAotCompatible=false` / `IsTrimmable=false`. Interpreted (non-AOT) browser-wasm works,
  and CI runs it, just not at JIT speed.

## Five places it goes beyond C#

The first four are the seam ([binders](binders.md)): binder rules for what C# would have failed
or got wrong. The fifth is translation. The argument rules (optional parameters, function and
delegate conversions) apply to every kind of call: instance and static methods, constructors,
delegate-typed members and delegate values.

### 1. F# function values can be called, and members read as functions

- A member holding an F# function value can be called: `e?Fn(21)`, a record field
  `h?OnPair(3, 4)`, `f |> Dlr.apply 21`. Curried or tupled, any arity.
- Any member, or the value itself (`Dlr.call f`), can be read as an F# function type:
  `let add: int -> int -> int = dlr { return w?Add }`. Curried or tupled, any arity.

C# has no form for either.

### 2. Functions and delegates convert both ways

An F# function fits a delegate parameter, and a delegate fits a function parameter. C#'s binder
sees an `FSharpFunc` and a `Func` as unrelated types.

- `x?Each(items, fun i -> …)` against an `Action<int>`; `x?Apply(3, Func<int, int>(…))` against
  an `int -> int`.
- It is the conversion F# does for a lambda at a static call, in both directions, with the
  reference-type variance delegates have: a parameter may be more general, a result more
  specific, value types exactly. A `Func<…, unit>` serves a `unit` result
  ([binders](binders.md)).
- The same holds for an assignment to a delegate- or function-typed property, field, indexer or
  array element: `x?Handler <- fun a b -> …`.

An F# function handed to a **dynamic object** (a script host's object, a `DynamicObject`)
arrives as the delegate of its own signature: `int -> unit` as an `Action<int>`. Every
meta-object understands delegates and none an `FSharpFunc`; there is no parameter type to drive
the conversion, so the function's own type does. That covers:

- an argument: `arr?forEach(fun n -> …)`;
- a value set on it: `el?onclick <- fun () -> …`;
- a handler added to its event: `com |> Dlr.addAssign "MoveComplete" (fun … -> …)`, COM's bound
  event included.

A CLR event takes one too: `Dlr.addAssign "Clicked" (fun sender n -> …)`, converted to the
event's delegate type. A new delegate is made per conversion, so to remove a handler with
`Dlr.subtractAssign`, add a delegate and keep it, as with a C# lambda.

### 3. F# optional parameters can be omitted

Omitted `?arg` parameters are `None`, and bare values become `Some`. C#'s binder cannot omit
them: they are `FSharpOption<'T>` parameters with no `[Optional]` metadata.

### 4. `?=?` and `?<?` are structural on F# types

Records, unions, tuples, lists, options, sets (anything without a CLR operator) compare as F#
`=` and `compare` do. C# would compare them by reference (`{ X = 1 } == { X = 1 }` is `false`
there) and has no `<` for them at all.

These keep C#'s rules: primitives, enums, strings (`==` only; `<` on strings and bools, which C#
lacks, is F#'s), types declaring `op_Equality`, and dynamic objects.

### 5. Names, type arguments and argument lists can come from data

C#'s are fixed at compile time. Here:

- `(?) x name` and `Dlr.typeArgsOf ts` create the call sites per distinct name or type list;
- `Dlr.namedOf kw` / `Dlr.argsOf xs` compile the call per distinct argument shape: keyword and
  positional arguments from data, like `f(*args, **kwargs)`.

Both are cached per site.

## Where the tests are

- The restrictions: `Tests/Restrictions.fs`, except
  - accessibility's protected cases: `Tests/FunctionMembers.fs`;
  - the one-shape markers' translation errors: `Tests/Invoke.fs`, `Tests/StaticOverloads.fs`
    and `Tests/Call.fs`;
  - a block in an `inline` function: the analyzer's tests;
  - NativeAOT: the assembly's own `IsAotCompatible=false`, not a test.
- The places it goes beyond: `Tests/FunctionMembers.fs`, `Tests/Delegates.fs`,
  `Tests/Operators.fs`, `Tests/ComputedNames.fs`, `Tests/TypeArgs.fs`, `Tests/ArgsFromData.fs`.
