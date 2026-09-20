# Syntax

Every form a block accepts, and what each binds to. The README has the common ones.

A target is any value; one that is not `obj` is upcast and binds on its runtime type, as `box x`
would. A member name, or the type-argument list of `Dlr.typeArgsOf`, may be a variable: the site
then creates its call sites per distinct name/types on first use (kept up to 256 keys, then
cleared), and a repeated key costs a dictionary lookup. `DlrCache.clear()` drops every compiled
block (for a host that unloads plugins; the next call recompiles), `DlrCache.count()` says how
many there are.

| Syntax | Binder |
| --- | --- |
| `x?Name` | GetMember, then Convert to the inferred type |
| `x?Name(a, b)`, `x?Name()`, `x?Name args` | InvokeMember. A `unit` argument — `()` or a `unit`-typed variable — is no arguments. Arguments keep their static type; `obj` arguments (`box a`, `a :> obj`) dispatch on the runtime type; literals get C#'s constant conversions (`5` to `byte`, `0` to an enum, `null` to any reference type). As in F#'s own method calls, a tuple-typed expression is several arguments (`let args = (1, 2)` then `x?Add args`) and a struct tuple is one; `box t` passes a tuple as one. A member holding an F# function value (curried or tupled) is applied when the binder cannot invoke it |
| `x?Name(a, Dlr.named {\| p = v \|})` | named arguments (a bare anonymous record is one positional argument) |
| `x?Name(Dlr.argsOf xs, Dlr.namedOf kw)`, `xs: obj list` | positional arguments whose count is a run-time value — Python's `*args`; `m?f(Dlr.argsOf args, Dlr.namedOf kwargs)` is `f(*args, **kwargs)`. Same per-shape compile and cache as `namedOf` (an empty name in the key is a positional); fixed arguments may come before or after it, named ones after |
| `x?Name(a, Dlr.namedOf kw)`, `kw: (string * obj) list` | named arguments whose names are run-time values — keyword arguments from data (`plt?plot(xs, ys, Dlr.namedOf kwargs)`); values dispatch on their runtime types; combines with positional arguments and `Dlr.named`; also in `Dlr.invoke`, `Dlr.new'`, static overloads, `Dlr.call` / `Dlr.apply`. The call is compiled once per distinct name list at the site (up to 64 kept), then a lookup. As with `Dlr.named`, a named call goes to C#'s binder unchanged, so an F# optional parameter (`?step`) must be supplied — the omit-it rule applies to positional calls only. Combines with a computed member name (`(?) x name (Dlr.namedOf kw)` — the function and its keyword arguments both from data) and with `Dlr.typeArgsOf` |
| `x?Name(Dlr.typeArgs<A, B>(), a)`, `x?Name(Dlr.typeArgsOf ts, a)` | explicit type arguments, first: `typeArgs` up to four; `typeArgsOf` any number, and its list may be a variable — types only known at run time, which C# `dynamic` cannot do (cached per site like a computed name); otherwise inferred from the arguments as in C# |
| `x?Name` typed `A -> B -> R` | a curried F# function that invokes the member when fully applied — a method, a delegate or an F# function alike — so `let add: int -> int -> int = dlr { return w?Add }`, then `add 1 2` or `add 1` partially; `A * B -> R` calls with a tuple; `unit -> R` reads a property or calls a parameterless method |
| `x?Name <- v` | SetMember |
| `(?) x name`, `((?) x name)(a)`, `(?<-) x name v` | the same three as plain function applications |
| `x \|> Dlr.get "Name"` | GetMember, target last, for pipelines; applied to arguments it invokes, like `?` |
| `x \|> Dlr.invoke "Name" (a, b)` | InvokeMember, target last |
| `x \|> Dlr.set "Name" v` | SetMember, target last |
| `x \|> Dlr.addAssign "Name" v`, `x \|> Dlr.subtractAssign "Name" v` | C#'s `+=` / `-=`: an IsEvent site picks the event accessor (`add_` / `remove_`) or read-modify-write |
| `Dlr.call x (a, b)`, `Dlr.call x ()`, `(x \|> Dlr.call) (a, b)` | Invoke the object itself — a delegate, a callable dynamic object, or an F# function value — the `?` of values: applied, it invokes, like `(x?Name)(a, b)`. Target first, as `?` is; the pipe form is `Dlr.apply` |
| `x \|> Dlr.apply (a, b)`, `x \|> Dlr.apply ()` | Invoke the object itself, target last for pipelines: the value's `Dlr.invoke` |
| `Dlr.call x` typed `A -> B -> R` | the target itself as that function, as `x?Name` typed so is the member: curried any arity, tupled up to five, `unit -> R` invokes with no arguments; a non-callable fails at the first application; an F# function of exactly that type is returned as it is. At a non-function type it is a translation error — a value read as a type is `Dlr.implicit` |
| `Dlr.Static<T>.Overloads?Name(a)`, `Dlr.Static<T>.Overloads \|> Dlr.invoke "Name" (a)` | a static overload set as the target, C#'s `T.Name(dynamicArg)`: the overload is chosen by the arguments' runtime types (multiple dispatch). Calls only — a static property is `T.P` in plain F# |
| `Dlr.new'<T>(a, b)`, `Dlr.new'<T>()`, `Dlr.new'<T> args` | InvokeConstructor, C#'s `new T(dynamicArg)`: the constructor overload is chosen by the arguments' runtime types (multiple dispatch). Arguments follow the member-call rules — a tuple variable is several, `Dlr.named`, `Dlr.namedOf` and `Dlr.argsOf` allowed; up to eight written out, any number through `Dlr.argsOf` |
| `x \|> Dlr.item i`, `x \|> Dlr.item (i, j)`, `x \|> Dlr.setItem (i, j) v` | GetIndex / SetIndex, target last; a tuple is several indexes |
| `?+? ?-? ?*? ?/? ?%? ?&&&? ?\|\|\|? ?^^^? ?<<<? ?>>>?` | BinaryOperation, then Convert |
| `?=? ?<>? ?<? ?>? ?<=? ?>=?` | BinaryOperation, then Convert to `bool` |
| `Dlr.neg x`, `Dlr.not x`, `Dlr.complement x` | UnaryOperation, then Convert |
| `Dlr.cast<T> x` | explicit Convert (a C# cast) |
| `Dlr.implicit x` | implicit Convert to the inferred type: widening, `op_Implicit`, `TryConvert` |

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
