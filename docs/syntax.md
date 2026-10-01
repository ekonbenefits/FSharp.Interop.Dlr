# Syntax

Every form a block accepts, and what each binds to. The README has the common ones.

A target is any value; one that is not `obj` is upcast and binds on its runtime type, as `box x`
would. A member name, or the type-argument list of `Dlr.typeArgsOf`, may be a variable: the site
then creates its call sites per distinct name/types on first use (kept up to 256 keys, then
cleared), and a repeated key costs a dictionary lookup. That is for a name chosen by
configuration, a small set; a name from data grows C#'s own symbol table for the life of the
process — index by key instead, `x |> Dlr.item key` ([caches](caches.md#bounds)).

A call evaluates its target first, then its arguments left to right, each once, as C# does —
also where a `Dlr.named` record, a splat list, a tuple or a computed name is involved; a bind
failure comes after the arguments have run.

`DlrCache.clear()` drops every compiled block (for a host that unloads plugins; the next call
recompiles), `DlrCache.count()` says how many there are.

| Syntax | Binder |
| --- | --- |
| `x?Name` | GetMember, then Convert to the inferred type |
| `x?Name(a, b)`, `x?Name()`, `x?Name args` | InvokeMember. A `unit` argument — `()`, a `unit`-typed variable, or a `unit`-valued expression (evaluated for its effect) — is no arguments. Arguments keep their static type; `obj` arguments (`box a`, `a :> obj`) dispatch on the runtime type; literals get C#'s constant conversions (`5` to `byte`, `0` to an enum, `null` to any reference type). As in F#'s own method calls, a tuple-typed expression is several arguments (`let args = (1, 2)` then `x?Add args`) and a struct tuple is one; `box t` passes a tuple as one. A member holding an F# function value (curried or tupled) is applied when the binder cannot invoke it |
| `x?Name(a, Dlr.named {\| p = v \|})` | named arguments (a bare anonymous record is one positional argument) |
| `x?Name(Dlr.argsOf xs, Dlr.namedOf kw)`, `xs: obj list` | positional arguments whose count is a run-time value — Python's `*args`; `m?f(Dlr.argsOf args, Dlr.namedOf kwargs)` is `f(*args, **kwargs)`. Same per-shape compile and cache as `namedOf` (an empty name in the key is a positional); fixed arguments may come before or after it, named ones after. At most 64 values (`NamedOfCache.MaxPositional`, an `ArgumentException` past it): each distinct count is a call-site arity kept for the life of the process ([caches](caches.md#bounds)), and a collection that could be long is one argument — an array to a `params` parameter, a list — not many |
| `let (found: bool), (v: int) = dlr { return d?TryGetValue(k, Dlr.out) }` | an `out` argument, returned as F# returns a method's out parameters: the result is the return value then each out as a tuple — or, when the result has no slot for the return value (a void method; any method whose return you do not want, discarded as a C# statement call's would be), the outs alone (the bare value for one). Each out's type is its element of the result type — C#'s dynamic call needs a written type for an out too (CS8197); a type not the parameter's exactly is the binder's error, as in C#. Every call form takes it — `x?M(…)` with a literal or computed name, `Dlr.get`, `Dlr.invoke`, `Dlr.Static<T>.Overloads`, `Dlr.call` / `Dlr.apply` — beside `Dlr.typeArgs` / `typeArgsOf` and `Dlr.named`; not `Dlr.new'<T>`, whose result is the `T` (a constructor's `ref` takes `Dlr.ref`), and not beside `Dlr.namedOf` / `Dlr.argsOf` (the outs' types are fixed by the result, a splat's arity is not) |
| `dlr { o?Swap(Dlr.ref a, Dlr.ref b) }`, `let mutable a = …` | a `ref` argument: the variable's value goes in and the method's write is assigned back. A `let mutable` only, in the block or captured; the same forms as `Dlr.out`, and `Dlr.new'` too. The value is read at the call, after every other argument — as C#'s reference sees each argument's write to it. The same variable twice (`Dlr.ref a, Dlr.ref a`) is one storage at the call, as in C#. Being a copy in and a copy back after the call, a method that throws leaves the variable as it was (C# keeps a write made before the throw) |
| `x?Name(a, Dlr.namedOf kw)`, `kw: (string * obj) list` | named arguments whose names are run-time values — keyword arguments from data (`plt?plot(xs, ys, Dlr.namedOf kwargs)`); values dispatch on their runtime types; combines with positional arguments and `Dlr.named`; also in `Dlr.invoke`, `Dlr.new'`, static overloads, `Dlr.call` / `Dlr.apply`. The call is compiled once per distinct name list at the site (up to 256 kept), then a lookup. As with `Dlr.named`, a named call goes to C#'s binder unchanged, so an F# optional parameter (`?step`) must be supplied — the omit-it rule applies to positional calls only. Combines with a computed member name (`(?) x name (Dlr.namedOf kw)` — the function and its keyword arguments both from data) and with `Dlr.typeArgsOf` |
| `x?Name(Dlr.typeArgs<A, B>(), a)`, `x?Name(Dlr.typeArgsOf ts, a)` | explicit type arguments, first: `typeArgs` up to four; `typeArgsOf` any number, and its list may be a variable — types only known at run time, which C# `dynamic` cannot do (cached per site like a computed name); otherwise inferred from the arguments as in C# |
| `x?Name` typed `A -> B -> R` | a curried F# function that invokes the member when fully applied — a method, a delegate or an F# function alike — so `let add: int -> int -> int = dlr { return w?Add }`, then `add 1 2` or `add 1` partially; `A * B -> R` calls with a tuple; `unit -> R` reads a property or calls a parameterless method |
| `x?Name <- v` | SetMember |
| `(?) x name`, `((?) x name)(a)`, `(?<-) x name v` | the same three as plain function applications |
| `x \|> Dlr.get "Name"`, `(x \|> Dlr.get "Add") (1, 2)` | GetMember, target last, for pipelines; applied to arguments it invokes, like `?` |
| `x \|> Dlr.invoke "Name" (a, b)` | InvokeMember, target last |
| `x \|> Dlr.set "Name" v` | SetMember, target last |
| `x \|> Dlr.addAssign "Name" v`, `x \|> Dlr.subtractAssign "Name" v` | C#'s `+=` / `-=`: an IsEvent site picks the event accessor (`add_` / `remove_`) or read-modify-write; an F# function handler converts to the event's delegate type, or for a dynamic object's (COM's) event to the delegate of its signature — keep a delegate to remove it later |
| `Dlr.call x (a, b)`, `Dlr.call x ()`, `(x \|> Dlr.call) (a, b)` | Invoke the object itself — a delegate, a callable dynamic object, or an F# function value — the `?` of values: applied, it invokes, like `(x?Name)(a, b)`. Target first, as `?` is; the pipe form is `Dlr.apply` |
| `x \|> Dlr.apply (a, b)`, `x \|> Dlr.apply ()` | Invoke the object itself, target last for pipelines: the value's `Dlr.invoke` |
| `Dlr.call x` typed `A -> B -> R` | the target itself as that function, as `x?Name` typed so is the member: curried any arity, tupled up to five, `unit -> R` invokes with no arguments; a non-callable fails at the first application; an F# function of exactly that type is returned as it is. At a non-function type it is a translation error — a value read as a type is `Dlr.implicit` |
| `Dlr.Static<T>.Overloads?Name(a)`, `Dlr.Static<T>.Overloads \|> Dlr.invoke "Name" (a)` | a static overload set as the target, C#'s `T.Name(dynamicArg)`: the overload is chosen by the arguments' runtime types (multiple dispatch). Calls only — a static property is `T.P` in plain F# |
| `Dlr.new'<T>(a, b)`, `Dlr.new'<T>()`, `Dlr.new'<T> args` | InvokeConstructor, C#'s `new T(dynamicArg)`: the constructor overload is chosen by the arguments' runtime types (multiple dispatch). Arguments follow the member-call rules — a tuple variable is several, `Dlr.named`, `Dlr.namedOf` and `Dlr.argsOf` allowed; up to eight written out, up to 64 through `Dlr.argsOf` |
| `x \|> Dlr.item i`, `x \|> Dlr.item (i, j)`, `x \|> Dlr.setItem (i, j) v` | GetIndex / SetIndex, target last; a tuple is several indexes |
| `?+? ?-? ?*? ?/? ?%? ?&&&? ?\|\|\|? ?^^^? ?<<<? ?>>>?` | BinaryOperation, then Convert |
| `?=? ?<>? ?<? ?>? ?<=? ?>=?` | BinaryOperation, then Convert to `bool` |
| `Dlr.neg x`, `Dlr.not x`, `Dlr.complement x` | UnaryOperation, then Convert |
| `Dlr.cast<T> x` | explicit Convert (a C# cast) |
| `Dlr.implicit x` | implicit Convert to the inferred type: widening, `op_Implicit`, `TryConvert` |
| `let! r = (dlr { return x?GetAsync(1) } : Task<int>)` | awaiting a dynamic call: a block is synchronous and returns what the member returns, converted to the awaitable type named — `Task<T>`, `Task`, `ValueTask<T>`, an F# `Async<'T>` — which `task { }` or `async { }` awaits as any other (`Async.AwaitTask` where F# needs it). A result type not known: `let t: Task = dlr { … }`, `do! t`, then `t?Result`. C# awaits the `dynamic` itself by binding the awaiter pattern at run time; naming the type here is the typed, faster spelling; an awaitable of unknown, non-`Task` type has no spelling and none is planned — nothing in practice returns one; a `ValueTask<T>` of unknown `T` is `?AsTask()` first (#110). A JS promise from ClearScript is bridged by its `JavaScriptExtensions.ToTask` — an extension method, so a static call between two blocks (`Tests/ClearScriptV8.fs`) |

Around them, ordinary F#: `let`, `let rec`, `use`, `if`, `for`, `while`, `try … with`,
`try … finally`, `let mutable` (inside the block or captured from outside, assigned anywhere in
it), and any code that quotations can express. Loop bodies reuse the block's call sites; a
`RuntimeBinderException` can be caught inside the block. Calling any operator or `Dlr.*` marker
outside a block throws `InvalidOperationException`; they exist only to be quoted.

Inside a block, ordinary F# is ordinary: `sprintf` and `$"…"`, `match` (literals, type tests),
records, unions, options, tuples, lists and arrays built from dynamic results, comprehensions
and `seq { }`, `List.map` with a lambda, `failwith`/`raise` (propagating as themselves, or
caught by the block's `try`), even an `async { }` or `task { }` — `Tests/FSharpInBlocks.fs`
pins each.
