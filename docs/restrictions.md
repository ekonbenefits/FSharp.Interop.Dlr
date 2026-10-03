# The same restrictions as C# `dynamic`, and where it goes beyond


Same binder, same limits — each pinned by a test in `Tests/Restrictions.fs` (accessibility's
protected cases in `Tests/FunctionMembers.fs`; the `inline` one by the analyzer's tests; NativeAOT
by the assembly's own `IsAotCompatible=false`, not a test):

- **Extension methods** are not found; the binder sees only the target's own members.
- **Static members** cannot be reached through an instance; static *calls* have their own target,
  `Dlr.Static<T>.Overloads`.
- **Explicitly implemented interface members** are not found: the binder sees the runtime type's
  public members, and an explicit implementation is a private method named `IFoo.Bar`. In F#
  every interface implementation is explicit, so `o?Dispose()` on an F# `IDisposable` fails
  unless the type also exposes the member; cast to the interface statically (`o :?> IFoo`) and
  call it there.
- **Accessibility is C#'s, from the calling type**: `private` binds only inside the declaring type,
  `internal` anywhere in the assembly, `protected` from a derived type through a receiver of
  that type (C#'s qualifier rule). F# `private` compiles to IL `internal`. The declaring
  type counts too: a public member of a type the calling type cannot see — another assembly's
  `internal` class, an anonymous type, a public generic over such a type — is not found,
  through the library's own rules as through C#'s binder; `[InternalsVisibleTo]` opens it, as
  for C#.
- **A block cannot live in an `inline` function or member** — in Release the function is
  expanded into every caller, taking the block's values with it and leaving its body behind (a
  Debug build calls it as a method, so it only appears to work); the analyzer reports it
  (`DLR004`, pinned by the analyzer's own tests rather than here). Not a C# restriction (C#
  has no `inline`), but the same family.
- **No compile-time checking**: a misspelt member or wrong arity is a `RuntimeBinderException`
  at the call.
- **Target and result are `obj`** (a typed target is upcast; the result converts to the inferred
  type), so value types box there; arguments do not. An `out` / `ref` parameter is `Dlr.out` /
  `Dlr.ref v` ([syntax](syntax.md)); a `byref` / `inref` / `outref` *value* cannot cross a dynamic
  operation (an `int[]` does reach a `Span<int>` parameter, through the implicit conversion, as in C#). Two consequences C# `dynamic` users know: a **struct target is a boxed
  copy**, so a mutating call through the box leaves the variable untouched; and a
  **`Nullable<T>` target erases** — the box holds a `T` or is `null`, so there is no `HasValue`
  or `Value` to find (the value reads as `T`).
- **`Dlr.named` takes the record literal itself** — the names are read from the quotation, so a
  record held in a variable is a translation error (the analyzer reports it); names from data
  are `Dlr.namedOf`. `Dlr.Static<T>.Overloads` is a call target only, and `Dlr.call x` is read
  at a function type or applied; each other use is a translation error the analyzer reports.
- **Generic type arguments** must be inferable from the arguments, or given explicitly —
  `Dlr.typeArgs<A, B>()` or `Dlr.typeArgsOf [ … ]`, whose list may even be a run-time value.
- **`inline` members with a member constraint** (`^T: (member Name: string)`) are found but
  throw `NotSupportedException` when called: their body only exists at inlining sites.
  Operator constraints (`v + v`) are fine, they resolve at run time.
- **No NativeAOT, no trimming.** The runtime binder, `LambdaExpression.Compile()` and the
  reflection that finds bodies and the captured variables' fields all need a JIT; the assembly is marked
  `IsAotCompatible=false` / `IsTrimmable=false`. Interpreted (non-AOT) browser-wasm works, and CI
  runs it, just not at JIT speed.

## Five places it goes beyond C#

The first four are the seam ([binders](binders.md)) — binder rules for what C# would have failed
or got wrong; the fifth is translation. The argument rules (optional parameters,
function/delegate conversion) apply to every kind of call: instance and static methods,
constructors, delegate-typed members and delegate values.

- **A member holding an F# function value can be called** (`e?Fn(21)`, a record field
  `h?OnPair(3, 4)`, `f |> Dlr.apply 21`), curried or tupled, any arity; and any member — or the
  value itself, `Dlr.call f` — can be read as an F# function type (`let add: int -> int -> int =
  dlr { return w?Add }`; curried or tupled, any arity), which C# has no form for.
- **An F# function fits a delegate parameter, and a delegate fits a function parameter**:
  `x?Each(items, fun i -> …)` against an `Action<int>`, `x?Apply(3, Func<int, int>(…))` against
  an `int -> int` — the conversions F# does at a static call — and the same for an assignment
  to a delegate- or function-typed property, field, indexer or array element (`x?Handler <- fun
  a b -> …`). C#'s binder sees an `FSharpFunc` and a `Func` as unrelated types. And **an F# function handed to a dynamic object** — a script
  host's object, a `DynamicObject` — arrives as the delegate of its own signature (`int -> unit`
  an `Action<int>`), as an argument (`arr?forEach(fun n -> …)`), a value set on it
  (`el?onclick <- fun () -> …`) or a handler added to its event (`com |> Dlr.addAssign
  "MoveComplete" (fun … -> …)`, COM's bound event included), since every meta-object understands
  delegates and none an `FSharpFunc`; there is no parameter type to drive it, so the function's
  own does. A CLR event takes one too (`Dlr.addAssign "Clicked" (fun sender n -> …)`, converted to
  the event's delegate type). A new delegate is made per conversion, so to remove a handler with
  `Dlr.subtractAssign`, add a delegate and keep it — as with a C# lambda.
- **F# optional parameters (`?arg`) can be omitted**: omitted ones are `None`, bare values become
  `Some`. C#'s binder cannot omit them (they are `FSharpOption<'T>` parameters with no `[Optional]`
  metadata).
- **`?=?` and `?<?` are structural on F# types**: records, unions, tuples, lists, options, sets
  — anything without a CLR operator — compare as F# `=` and `compare` do. C# would compare
  them by reference (`{ X = 1 } == { X = 1 }` is `false` there) and has no `<` for them at all.
  Primitives, enums, strings (`==` only; `<` on strings and bools, which C# lacks, is F#'s), types declaring
  `op_Equality` and dynamic objects keep C#'s rules.
- **Member names, generic type arguments and argument names may be run-time values**: `(?) x name`
  and `Dlr.typeArgsOf ts` create the call sites per distinct name or type list, and
  `Dlr.namedOf kw` / `Dlr.argsOf xs` compile the call per distinct argument shape (keyword
  arguments and positional arguments from data, `f(*args, **kwargs)`), cached per site. C#'s are fixed at compile time.
