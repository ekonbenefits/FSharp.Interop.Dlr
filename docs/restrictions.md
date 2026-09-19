# The same restrictions as C# `dynamic`, and where it goes beyond


Same binder, same limits — each pinned by a test in `Tests/Restrictions.fs`:

- **Extension methods** are not found; the binder sees only the target's own members.
- **Static members** cannot be reached through an instance; static *calls* have their own target,
  `Dlr.Static<T>.Overloads`.
- **Explicitly implemented interface members** are not found: the binder sees the runtime type's
  public members, and an explicit implementation is a private method named `IFoo.Bar`. In F#
  every interface implementation is explicit, so `o?Dispose()` on an F# `IDisposable` fails
  unless the type also exposes the member; cast to the interface statically (`o :?> IFoo`) and
  call it there.
- **Accessibility is the calling type's**: `private` binds only inside the declaring type,
  `internal` anywhere in the assembly. F# `private` compiles to IL `internal`.
- **No compile-time checking**: a misspelt member or wrong arity is a `RuntimeBinderException`
  at the call.
- **Target and result are `obj`**, so value types box there; arguments do not. `byref` and
  `Span` cannot cross a dynamic operation.
- **Generic type arguments** must be inferable from the arguments, or given explicitly —
  `Dlr.typeArgs<A, B>()` or `Dlr.typeArgsOf [ … ]`, whose list may even be a run-time value.
- **`inline` members with a member constraint** (`^T: (member Name: string)`) are found but
  throw `NotSupportedException` when called: their body only exists at inlining sites.
  Operator constraints (`v + v`) are fine, they resolve at run time.
- **No NativeAOT, no trimming.** The runtime binder, `LambdaExpression.Compile()` and the
  reflection that finds bodies and closure fields all need a JIT; the assembly is marked
  `IsAotCompatible=false` / `IsTrimmable=false`. Interpreted (non-AOT) browser-wasm works, and CI
  runs it, just not at JIT speed.

## Five places it goes beyond C#

The first four are binder rules for what C# would have failed or got wrong, so nothing C# binds
correctly changes. The argument rules (optional parameters,
function/delegate conversion) apply to every kind of call: instance and static methods,
constructors, delegate-typed members and delegate values.

- **A member holding an F# function value can be called** (`e?Fn(21)`, a record field
  `h?OnPair(3, 4)`, `f |> Dlr.call 21`), curried or tupled, any arity; and any member can be read
  as an F# function type (`let add: int -> int -> int = dlr { return w?Add }`; curried any
  arity, tupled up to five), which C# has no form for.
- **An F# function fits a delegate parameter, and a delegate fits a function parameter**:
  `x?Each(items, fun i -> …)` against an `Action<int>`, `x?Apply(3, Func<int, int>(…))` against
  an `int -> int` — the conversions F# does at a static call. C#'s binder sees an `FSharpFunc`
  and a `Func` as unrelated types.
- **F# optional parameters (`?arg`) can be omitted**: omitted ones are `None`, bare values become
  `Some`. C#'s binder cannot omit them (they are `FSharpOption<'T>` parameters with no `[Optional]`
  metadata).
- **`?=?` and `?<?` are structural on F# types**: records, unions, tuples, lists, options, sets
  — anything without a CLR operator — compare as F# `=` and `compare` do. C# would compare
  them by reference (`{ X = 1 } == { X = 1 }` is `false` there) and has no `<` for them at all.
  Primitives, enums, strings (`==` only; `<` on strings and bools, which C# lacks, is F#'s), types declaring
  `op_Equality` and dynamic objects keep C#'s rules.
- **Member names and generic type arguments may be run-time values**: `(?) x name` and
  `Dlr.typeArgsOf ts` create the call sites per distinct name or type list, cached per site. C#'s
  are fixed at compile time.
