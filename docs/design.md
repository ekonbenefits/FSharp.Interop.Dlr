# Why `[<ReflectedDefinition>]` and not quotations

`dlr { }` reads each block's body from the `[<ReflectedDefinition>]` of the function or member
around it. An obvious alternative would be to have the builder take the block as a quotation: an
auto-quoted `[<ReflectedDefinition>] Expr` parameter, or a `Quote` member on the builder. This page
explains why the library doesn't do that, and what it costs. Part of [internals](internals.md).

## The short version

| Need | Reflected definition of the enclosing member | Quotation passed to the builder |
|---|---|---|
| Decode cost | Once per type, then cached | Unpickled again on every evaluation |
| Steady-state allocation | None in Release (captures live in the state-machine struct) | New nodes for every splice, value-type captures boxed |
| Binder accessibility context | The member's declaring type | Not available: the quote doesn't say where it came from |
| Works with resumable code | Yes | No: quoted code isn't lowered to a state machine |

How a block runs from there (the state machine, the cache, translation on the first call) is the
[pipeline](pipeline.md).

## It was tried

A quoted builder, `dlrq { }` with a `Quote` member, was built and reviewed
([#60](https://github.com/ekonbenefits/FSharp.Interop.Dlr/issues/60),
[#62](https://github.com/ekonbenefits/FSharp.Interop.Dlr/issues/62)), then dropped. It worked: the
same translator and binders compiled it, and the whole test suite passed through it. What stopped
it is below. It cost 8.2 µs per `w?Count` against 20 ns for `dlr { }` at the time, 7.3 µs of that
FSharp.Core materialising the quotation literal, but the blocker was accessibility.

## Why not a quotation

**Decode cost on every call.** A quotation literal compiles to a call that deserializes it from a
freshly allocated byte array, and each evaluation unpickles the tree and resolves its types and
members through reflection again: 7.4 µs measured for a one-operation block, about 25 µs for a
100-iteration loop body. FSharp.Core caches only reflected definitions
([dotnet/fsharp#20764](https://github.com/dotnet/fsharp/issues/20764) asks for literals to be
unpickled once). For a library whose steady state is about 11 ns per call
([benchmarks](benchmarks.md)), a quotation per call is out of the question.

**Allocation, even if the tree were cached.** Captured values are spliced into the tree as
`ValueWithName` nodes. Every evaluation builds new nodes around them and boxes value-type
captures. The state machine instead hands the library its captures unboxed in a struct, with no
tree.

**No call-site context.** C# `dynamic` passes the enclosing type to the binder as its
accessibility context, which decides whether `internal` and `private` members are visible, and
`dlr { }` does the same ([restrictions](restrictions.md)). A reflected definition belongs to a
member, so its declaring type is exactly that context. A quotation arrives with no record of its
caller. The quoted builder found no sound way to recover one: a stack walk fails in Release, where `Run` is a
tail call; the expression keeps no reference to its assembly; and the caller-info attributes give
only a file, a line and a member name. With `obj` as the context, an `internal` type is
unreachable even for its public members, and so are F# `private` members, which is most
application code.

**Quoted code has restrictions of its own.** The compiler rejects assigning a captured mutable and
calling a member on a captured value-type local inside a quotation (FS3155); both work in a
`dlr { }` block.

**Quoting and resumable code don't mix.** A `Quote` member turns the whole computation expression
into an `Expr` passed to `Run`. Resumable-code lowering applies only to compiled inline code, so a
quoted block can never be a state machine.

## What this choice costs

**The enclosing member needs the attribute.** Without it, the first call raises a
`DlrTranslationException`; the analyzer package reports it at build time instead, with a fix. Some
F# has no quotation form (inner generic functions, byrefs, `Span`), so a block may need to move
into the smallest function that can be quoted ([restrictions](restrictions.md)).

**It relies on compiler layout that isn't documented.** Binding the translated tree to captured
values means matching the quotation's free variables to fields of the compiled state machine (or
the closure, where the compiler builds no machine). The compiler chooses those fields after
optimization and doesn't guarantee their shape. [Translation](translation.md#what-the-compiler-is-assumed-to-do)
lists every assumption the translator makes. To catch drift, CI builds and runs the tests with the
SDK 8, 9, 10 and 11 compilers, which differ in what they build in Debug.

**One undecodable member can affect the whole assembly.** FSharp.Core decodes an assembly's
reflected definitions together. If any reflected body calls a function its signature file hides,
which the Release optimizer may inline and erase, every block in the assembly fails
([#213](https://github.com/ekonbenefits/FSharp.Interop.Dlr/issues/213)). The analyzer warns about
it (DLR008), and the run-time error names the function: add it to the signature, or mark it
`[<MethodImpl(MethodImplOptions.NoInlining)>]`.

**The closure path allocates.** Where the compiler builds no state machine (Debug on some
compilers, and one shape in Release), the block runs on its closure: the same results, a little
slower, with an allocation per call. The allocation-free steady state is the state-machine path's.

## What would change this decision

Moving to quotations would need all three of these from the compiler and FSharp.Core:

1. quotation literals unpickled once rather than per evaluation
   ([dotnet/fsharp#20764](https://github.com/dotnet/fsharp/issues/20764)),
2. call-site context (the enclosing type or method) carried with an auto-quoted expression,
3. captures delivered separately from the cached tree, unboxed, so that evaluation doesn't
   allocate.

Alternatively, the current approach would lose its main fragility if the compiler documented the
mapping from source variables to closure and state-machine fields for code under
`[<ReflectedDefinition>]`.
