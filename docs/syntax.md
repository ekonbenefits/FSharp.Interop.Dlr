# Syntax

Every form a block accepts, and what each binds to. The README has the common ones.

A member name, or the type-argument list of `Dlr.typeArgsOf`, may be a variable: the site then
creates its call sites per distinct name/types on first use (kept up to 256 keys, then cleared),
and a repeated key costs a dictionary lookup.

| Syntax | Binder |
| --- | --- |
| `x?Name` | GetMember, then Convert to the inferred type |
| `x?Name(a, b)`, `x?Name()`, `x?Name args` | InvokeMember. Arguments keep their static type; `obj` arguments (`box a`, `a :> obj`) dispatch on the runtime type; literals get C#'s constant conversions (`5` to `byte`, `0` to an enum, `null` to any reference type). As in F#'s own method calls, a tuple-typed expression is several arguments (`let args = (1, 2)` then `x?Add args`) and a struct tuple is one; `box t` passes a tuple as one. A member holding an F# function value (curried or tupled) is applied when the binder cannot invoke it |
| `x?Name(a, Dlr.named {\| p = v \|})` | named arguments (a bare anonymous record is one positional argument) |
| `x?Name(Dlr.typeArgs<A, B>(), a)`, `x?Name(Dlr.typeArgsOf ts, a)` | explicit type arguments, first: `typeArgs` up to four; `typeArgsOf` any number, and its list may be a variable — types only known at run time, which C# `dynamic` cannot do (cached per site like a computed name); otherwise inferred from the arguments as in C# |
| `x?Name` typed `A -> B -> R` | a curried F# function that invokes the member when fully applied — a method, a delegate or an F# function alike — so `let add: int -> int -> int = dlr { return w?Add }`, then `add 1 2` or `add 1` partially; `A * B -> R` calls with a tuple; `unit -> R` reads a property or calls a parameterless method |
| `x?Name <- v` | SetMember |
| `(?) x name`, `((?) x name)(a)`, `(?<-) x name v` | the same three as plain function applications |
| `x \|> Dlr.get "Name"` | GetMember, target last, for pipelines; applied to arguments it invokes, like `?` |
| `x \|> Dlr.invoke "Name" (a, b)` | InvokeMember, target last |
| `x \|> Dlr.set "Name" v` | SetMember, target last |
| `x \|> Dlr.addAssign "Name" v`, `x \|> Dlr.subtractAssign "Name" v` | C#'s `+=` / `-=`: an IsEvent site picks the event accessor (`add_` / `remove_`) or read-modify-write |
| `x \|> Dlr.call (a, b)`, `x \|> Dlr.call ()` | Invoke the object itself: a delegate, a callable dynamic object, or an F# function value |
| `Dlr.Static<T>.Overloads?Name(a)`, `Dlr.Static<T>.Overloads \|> Dlr.invoke "Name" (a)` | a static overload set as the target, C#'s `T.Name(dynamicArg)`: the overload is chosen by the arguments' runtime types (multiple dispatch). Calls only — a static property is `T.P` in plain F# |
| `Dlr.new'<T>(a, b)`, `Dlr.new'<T>()` | InvokeConstructor, C#'s `new T(dynamicArg)`: the constructor overload is chosen by the arguments' runtime types (multiple dispatch); up to eight arguments, `Dlr.named` allowed |
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
