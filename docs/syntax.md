# Syntax

Every form a block accepts, and what each binds to. The README has the common ones.

## Blocks

```fsharp
open FSharp.Interop.Dlr

[<ReflectedDefinition>]
let greet (o: obj) : string = dlr { return o?Greet("world") }
```

A block is `dlr { … }`. The library compiles it from the stored body of the function or member
around it, once per site, so that function carries `[<ReflectedDefinition>]`. Every form on this
page goes inside a block: called outside one, a marker throws `InvalidOperationException`.

### Where the attribute goes

On the function or member that holds the block:

```fsharp
[<ReflectedDefinition>]
let total (rows: obj) : decimal = dlr { return rows?Sum("Amount") }

type Report(data: obj) =
    [<ReflectedDefinition>]
    member _.Total: decimal = dlr { return data?Total }
```

- A module- or type-level attribute also works, but it stores a quotation of everything under
  it, and ordinary F# often has no quotation form: inner generic functions, `byref`s, `Span`. So
  prefer the binding.
- If the function around a block cannot be quoted, move the block into the smallest function
  that can.
- A local function inside it is a closure and cannot carry the attribute; the outermost binding
  does.
- A block in module-level `do` code has no function to carry it: move it into one.
- Without the attribute, the first call raises a `DlrTranslationException` that says so.

### One block per line

A block is found by the file and line of its `Run` call, so two blocks cannot start on one source
line: the first call raises a `DlrTranslationException`. Put each block on its own line.

### Not in an `inline` function

A block cannot live in an `inline` function or member, declaration-level or a local
`let inline`. In Release the function is expanded into every caller, where the block's captured
values are inlined away and its body is not where the reflected definition says. A Debug build
calls it as a method, so it only appears to work. Remove `inline` or move the block out.

### The analyzer

The `FSharp.Interop.Dlr.Analyzers` package reports each of these at build time (and in Ionide),
rather than at the first call:

| Code | Reports |
| --- | --- |
| `DLR001` | a block with no `[<ReflectedDefinition>]` around it (with a fix that adds it) |
| `DLR002` | a `?` operator or `Dlr.*` marker outside any block |
| `DLR003` | two or more blocks starting on one line |
| `DLR004` | a block in an `inline` function or member |
| `DLR005` | a marker out of place inside a block: the shapes this page calls translation errors |
| `DLR006` | a block in a member whose reflected definition FSharp.Core will not decode (it holds `typeof<System.Void>`) |

Setup, and the full list of `DLR005` cases, are in the
[analyzer's README](../FSharp.Interop.Dlr.Analyzers/README.md).

## Targets, names and order

A target is any value; one that is not `obj` is upcast and binds on its runtime type, as `box x`
would.

A member name, or the type-argument list of `Dlr.typeArgsOf`, may be a variable. The site then
creates its call sites per distinct name or type list on first use (up to 256 keys, then
cleared), and a repeated key costs a dictionary lookup. That is for a name chosen by
configuration, a small set. A name from data grows C#'s own symbol table for the life of the
process: index by key instead, `x |> Dlr.item key` ([caches](caches.md#bounds)).

A call evaluates its target first, then its arguments left to right, each once, as C# does. That
holds where a `Dlr.named` record, a splat list, a tuple or a computed name is involved too. A bind
failure comes after the arguments have run.

`DlrCache.clear()` drops every compiled block (for a host that unloads plugins; the next call
recompiles), and `DlrCache.count()` says how many there are.

## How a result gets its type

Every marker's result is a type parameter: `(?)` is `obj -> string -> 'TResult`. So F# infers
the result from how it is used, as for any generic function, and the block compiles a conversion
to that type: C#'s implicit conversion, as `int n = d.Count;` is in C#. The type can come from:

- the binding: `let n: int = dlr { return x?Count }`;
- the block: `(dlr { return x?Count } : int)`;
- a binding inside the block: `dlr { let n: int = x?Count in … }`;
- the context: `n + 1` with `n: int`, or an argument to a function that takes an `int`.

A call is the same rule one step on. F# reads `x?Add(1, 2)` as applying `x?Add` to `(1, 2)`, so
the member's type is inferred as a function, `int * int -> 'R`, and `'R` is the call's result.

What the inferred type selects:

- **`obj`**: no conversion; the value as the binder returned it. A result nothing constrains is
  `obj` too: `let v = x?Count`, used only by `printfn "%A" v`, is an `obj` holding an `int`.
- **`unit`**: the result is discarded (a void call site).
- **a function type**: the member read as a function ([more](#members-as-functions)).
- **an awaitable**: a `Task` and friends ([more](#awaiting)).
- **a tuple, with `Dlr.out`**: the return value, then the outs ([more](#dlrout)).
- **any other type**: the conversion. A value that does not convert is C#'s
  `RuntimeBinderException` at the call: `let s: string = dlr { return x?Count }` throws "Cannot
  implicitly convert type 'int' to 'string'". For an explicit conversion (a C# cast), use
  `Dlr.cast<T>`.

A function that returns a block, with no annotation anywhere, is generic:
`let count () = dlr { return x?Count }` is `unit -> 'a`, and each caller's type decides.

## Forms

In the forms, `x` is the target, `Name` a member name as written, `name` a `string` variable,
`a` and `b` arguments, `v` a value, `i` and `j` indexes, and `T`, `A`, `B` and `R` types.

| Syntax | Binder |
| --- | --- |
| `x?Name` | GetMember, then Convert to the inferred type |
| `x?Name(a, b)`, `x?Name()`, `x?Name args` | InvokeMember ([arguments](#arguments)) |
| `x?Name(a, Dlr.named {\| p = v \|})` | named arguments (a bare anonymous record is one positional argument) |
| `x?Name(a, Dlr.namedOf kw)` | named arguments with run-time names; `kw` is a `(string * obj) list` ([more](#dlrnamedof)) |
| `x?Name(Dlr.argsOf xs, Dlr.namedOf kw)` | positional arguments with a run-time count, Python's `*args`; `xs` is an `obj list` ([more](#dlrargsof)) |
| `x?Name(a, Dlr.out)` | an `out` argument, returned in the result ([more](#dlrout)) |
| `x?Name(Dlr.outAs<T> ())` | an `out` argument whose type is stated ([more](#dlroutas)) |
| `x?Name(Dlr.ref a)` | a `ref` argument; `a` is a `let mutable`, written back ([more](#dlrref)) |
| `x?Name(Dlr.typeArgs<A, B>(), a)`, `x?Name(Dlr.typeArgsOf ts, a)` | explicit type arguments ([more](#type-arguments)) |
| `let f: A -> B -> R = dlr { return x?Name }` | the member as an F# function ([more](#members-as-functions)) |
| `x?Name <- v` | SetMember |
| `(?) x name`, `((?) x name)(a)`, `(?<-) x name v` | the same three as plain function applications |
| `x \|> Dlr.get "Name"`, `(x \|> Dlr.get "Add") (1, 2)` | GetMember, target last; applied to arguments, it invokes, like `?`; at a function type, the member as a function |
| `x \|> Dlr.invoke "Name" (a, b)` | InvokeMember, target last; the name may be computed; [arguments](#arguments) as for `?` |
| `x \|> Dlr.set "Name" v` | SetMember, target last |
| `x \|> Dlr.addAssign "Name" v`, `x \|> Dlr.subtractAssign "Name" v` | C#'s `+=` / `-=` ([more](#events)) |
| `Dlr.call x (a, b)`, `Dlr.call x ()`, `(x \|> Dlr.call) (a, b)` | Invoke the object itself ([more](#invoking-a-value)) |
| `x \|> Dlr.apply (a, b)`, `x \|> Dlr.apply ()` | Invoke the object itself, target last |
| `let f: A -> B -> R = dlr { return Dlr.call x }` | the value as an F# function ([more](#invoking-a-value)) |
| `Dlr.Static<T>.Overloads?Name(a)`, `Dlr.Static<T>.Overloads \|> Dlr.invoke "Name" (a)` | C#'s `T.Name(dynamicArg)`: a static overload by runtime types ([more](#static-calls-and-constructors)) |
| `Dlr.new'<T>(a, b)`, `Dlr.new'<T>()`, `Dlr.new'<T> args` | C#'s `new T(dynamicArg)`: InvokeConstructor ([more](#static-calls-and-constructors)) |
| `x \|> Dlr.item i`, `x \|> Dlr.item (i, j)`, `x \|> Dlr.setItem (i, j) v` | GetIndex / SetIndex, target last; a tuple (literal or variable) is several indexes, a struct tuple one |
| `a ?+? b` | BinaryOperation, then Convert; likewise `?-?` `?*?` `?/?` `?%?` `?&&&?` `?\|\|\|?` `?^^^?` `?<<<?` `?>>>?` |
| `a ?=? b` | BinaryOperation, then Convert to `bool`; likewise `?<>?` `?<?` `?>?` `?<=?` `?>=?` |
| `Dlr.neg x`, `Dlr.not x`, `Dlr.complement x` | UnaryOperation, then Convert |
| `Dlr.cast<T> x` | explicit Convert (a C# cast) |
| `Dlr.implicit x` | implicit Convert to the inferred type: widening, `op_Implicit`, `TryConvert` |
| `(dlr { return x?Name(a) } : Task<T>)` | a dynamic call to await ([more](#awaiting)) |

## Details

### Arguments

- A `unit` argument is no arguments: `()`, a `unit`-typed variable, or a `unit`-valued expression
  (evaluated for its effect).
- Arguments keep their static type. `obj` arguments (`box a`, `a :> obj`) dispatch on the
  runtime type.
- Literals get C#'s constant conversions: `5` to `byte`, `0` to an enum, `null` to any
  reference type.
- As in F#'s own method calls, a tuple-typed expression is several arguments (`let args = (1, 2)`
  then `x?Add args`), and a struct tuple is one. `box t` passes a tuple as one.
- A member holding an F# function value (curried or tupled) is applied when the binder cannot
  invoke it.

### `Dlr.namedOf`

Keyword arguments from data: `plt?plot(xs, ys, Dlr.namedOf kwargs)`.

- Values dispatch on their runtime types.
- Combines with positional arguments and `Dlr.named`, with a computed member name
  (`(?) x name (Dlr.namedOf kw)`: the function and its keyword arguments both from data) and
  with `Dlr.typeArgsOf`.
- Also in `Dlr.invoke`, `Dlr.new'`, static overloads, `Dlr.call` / `Dlr.apply`.
- The call is compiled once per distinct name list at the site (up to 256 kept), then a lookup.
- As with `Dlr.named`, a named call goes to C#'s binder unchanged, so an F# optional parameter
  (`?step`) must be supplied: the omit-it rule applies to positional calls only.

### `Dlr.argsOf`

Positional arguments whose count is a run-time value: `m?f(Dlr.argsOf args, Dlr.namedOf kwargs)`
is Python's `f(*args, **kwargs)`.

- Compiled and cached per shape, as `namedOf` is; an empty name in the key is a positional.
- Fixed arguments may come before or after it; named ones after.
- At most 64 values (`NamedOfCache.MaxPositional`; an `ArgumentException` past it). Each distinct
  count is a call-site arity kept for the life of the process ([caches](caches.md#bounds)), so a
  collection that could be long is one argument (an array to a `params` parameter, a list), not
  many.

### `Dlr.out`

`let (found: bool), (v: int) = dlr { return d?TryGetValue(k, Dlr.out) }`

An `out` argument is returned as F# returns a method's out parameters. The result is:

- the return value, then each out, as a tuple: a reference tuple, or a struct one
  (`let struct (found, v) = …`, which allocates no tuple);
- or the outs alone (the bare value for one), when the result has no slot for the return value:
  a void method, or any method whose return you do not want, discarded as a C# statement call's
  would be.

Each out's type is its element of the result type. C#'s dynamic call needs a written type for an
out too (CS8197); a type that is not exactly the parameter's is the binder's error, as in C#.

Every call form takes it: `x?M(…)` with a literal or computed name, `Dlr.get`, `Dlr.invoke`,
`Dlr.Static<T>.Overloads`, `Dlr.call` / `Dlr.apply`, beside `Dlr.typeArgs` / `typeArgsOf` and
`Dlr.named`. Not:

- `Dlr.new'<T>`, whose result is the `T` (a constructor's `ref` takes `Dlr.ref`);
- beside `Dlr.namedOf` / `Dlr.argsOf`: the outs' types are fixed by the result, and a splat's
  arity is not.

### `Dlr.outAs`

`let v: struct (int * int) = dlr { return o?PairOut(Dlr.outAs<struct (int * int)> ()) }`

An `out` argument whose type is stated rather than inferred from the result's shape. The shapes
are tried in order, and the first that agrees with every stated type wins:

1. the return value, then the outs;
2. the outs alone;
3. the one out's bare value.

It is needed where `Dlr.out` reads the other way: a lone out that is itself a tuple, read as the
bare value. With `Dlr.out`, a two-element result is the return value then the out, and a tuple
result is the bare value only when `Dlr.outAs` states its type.

The type is written: `Dlr.outAs<_> ()` is `obj`, not "from the result"; that is `Dlr.out`.

### `Dlr.ref`

`let mutable a = 1` … `dlr { o?Swap(Dlr.ref a, Dlr.ref b) }`

A `ref` argument: the variable's value goes in and the method's write is assigned back.

- A `let mutable` only, in the block or captured.
- The same forms as `Dlr.out`, and `Dlr.new'` too.
- The value is read at the call, after every other argument, as C#'s reference sees each
  argument's write to it.
- The same variable twice (`Dlr.ref a, Dlr.ref a`) is one storage at the call, as in C#.
- It is a copy in and a copy back after the call, so a method that throws leaves the variable as
  it was. (C# keeps a write made before the throw.)

### Type arguments

Explicit type arguments come first. `typeArgs` takes up to four. `typeArgsOf` takes any number,
and its list may be a variable: types only known at run time, which C# `dynamic` cannot do
(cached per site like a computed name). Otherwise type arguments are inferred from the
arguments, as in C#.

### Members as functions

`x?Name` typed `A -> B -> R` is a curried F# function that invokes the member when fully applied,
whether the member is a method, a delegate or an F# function. So
`let add: int -> int -> int = dlr { return w?Add }`, then `add 1 2`, or `add 1` partially.

- `A * B -> R` calls with a tuple.
- `unit -> R` reads a property or calls a parameterless method.

### Events

`Dlr.addAssign` / `Dlr.subtractAssign` are C#'s `+=` / `-=`. An IsEvent site picks the event
accessor (`add_` / `remove_`) or a read-modify-write.

An F# function handler converts to the event's delegate type, or for a dynamic object's (COM's)
event, to the delegate of its signature. Keep a delegate to remove it later.

### Invoking a value

`Dlr.call x (a, b)` invokes the object itself: a delegate, a callable dynamic object, or an F#
function value. It is the `?` of values: applied, it invokes, like `(x?Name)(a, b)`. Target first,
as `?` is; the pipe form is `Dlr.apply`, which is the value's `Dlr.invoke`.

`Dlr.call x` typed `A -> B -> R` is the target itself as that function, as `x?Name` typed so is
the member:

- curried or tupled, any arity; `unit -> R` invokes with no arguments;
- a non-callable fails at the first application;
- an F# function of exactly that type is returned as it is;
- at a non-function type it is a translation error: a value read as a type is `Dlr.implicit`.

### Static calls and constructors

`Dlr.Static<T>.Overloads` is a static overload set as the target, C#'s `T.Name(dynamicArg)`. The
overload is chosen by the arguments' runtime types (multiple dispatch). A computed name,
`Dlr.typeArgs` / `typeArgsOf`, `Dlr.named` / `namedOf` / `argsOf` and `Dlr.out` work as on a
member call. Calls only: a static property is `T.P` in plain F#.

`Dlr.new'<T>` is InvokeConstructor, C#'s `new T(dynamicArg)`: the constructor overload is chosen
by the arguments' runtime types. Arguments follow the member-call rules: a tuple variable is
several, and `Dlr.named`, `Dlr.namedOf` and `Dlr.argsOf` are allowed. Up to eight written out, up
to 64 through `Dlr.argsOf`.

### Awaiting

`let! r = (dlr { return x?GetAsync(1) } : Task<int>)`

A block is synchronous and returns what the member returns, converted to the awaitable type
named: `Task<T>`, `Task`, `ValueTask<T>`, an F# `Async<'T>`. `task { }` or `async { }` awaits it
as any other (`Async.AwaitTask` where F# needs it).

- A result type not known: `let t: Task = dlr { … }`, `do! t`, then `t?Result`.
- A `ValueTask<T>` of unknown `T` is `?AsTask()` first (#110).
- An awaitable of unknown, non-`Task` type has no spelling, and none is planned: nothing in
  practice returns one.
- A JS promise from ClearScript is bridged by its `JavaScriptExtensions.ToTask`, an extension
  method, so a static call between two blocks (`Tests/ClearScriptV8.fs`).

C# awaits the `dynamic` itself, binding the awaiter pattern at run time; naming the type here is
the typed, faster spelling.

## Around the forms

Around them, ordinary F#: `let`, `let rec`, `use`, `if`, `for`, `while`, `try … with`,
`try … finally`, `let mutable` (inside the block or captured from outside, assigned anywhere in
it), and any code that quotations can express. Loop bodies reuse the block's call sites; a
`RuntimeBinderException` can be caught inside the block.

Inside a block, ordinary F# is ordinary: `sprintf` and `$"…"`, `match` (literals, type tests),
records, unions, options, tuples, lists and arrays built from dynamic results, comprehensions
and `seq { }`, `List.map` with a lambda, `failwith`/`raise` (propagating as themselves, or
caught by the block's `try`), even an `async { }` or `task { }`. `Tests/FSharpInBlocks.fs` pins
each.
