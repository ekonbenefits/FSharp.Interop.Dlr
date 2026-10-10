# The build companion

An opt-in build step, for 2.0 (1.0 is attribute-only; the companion is developed on the `v2`
branch). Everything works without it, and a `dlr { }` block that sticks to the shapes in
[restrictions](restrictions.md) never needs it. It answers one question exactly that the run time
otherwise answers by inference or refuses: which source variable each field of a block's state
machine holds. Its `body` section, from the same build step, lets a block do without
`[<ReflectedDefinition>]`.

## The problem it answers

In Release a block is a struct state machine whose fields are its captured variables
([pipeline](pipeline.md)). The fields are named after the optimized expression's free locals, so
a block reaching two variables of one name (`x` and `x0`, a split tuple's `x_0` and `x_00`) is
ambiguous by name, and the run time refuses it ([translation](translation.md)). Inferring the
mapping from the IL was tried and rejected: each review round found a new way it could bind a
variable to the wrong field. The compiler, though, knows the answer when it builds the struct.

## What it computes

`FSharp.Interop.Dlr.Build` type-checks the project with FCS once (`Check.project`, the project's
own fsc arguments, and the FCS of the compiler that builds it) and runs a pass per map section over that one check.

The `fields` pass (`Fields.run`) reads FCS's optimized tree and, for each block, takes the state
machine's fields the way code generation makes them: the free locals of the block's
`__stateMachine` expression after the lowering inlines let-bound resumable code, without the
values stored as methods (lambda-lifted `f@12`), ordered by the compiler's stamp (read by
reflection) and named by the compiler's own unique-naming rule (`x`, `x0`, …). It then attributes
each field to a source variable, identified as the run time can find it again in the quotation:
its name and its order among the member's bindings of that name. Each rule gives an exact answer
or none:

- a variable of the source is itself;
- an optimizer temporary (`x_0`, a split tuple's element) is that element of the source tuple
  whose value holds its range; a tuple parameter the compiler split counts as the quotation's one
  tuple only when its elements are compiler-generated (a user's own `(x_0, x_1)` is not a split);
- a closure's copy of a captured variable (a local function or lambda made a closure, or lifted:
  a new value with the enclosing function's range, or a lambda parameter of the optimized member)
  is the one source variable of that name declared before it; a captured `mutable` arrives as its
  ref cell;
- a copy let-bound to another value is that value's attribution;
- anything else: none, and the block is left out of the map.

Nested blocks share a file and line, and only the outermost is ever looked up (an inner one
compiles as part of it), so per line the map keeps the block whose `dlr` comes first.

The `body` pass (`Bodies.run`, stacked on this work) records each block's member body from the
unoptimized tree, so a block needs no `[<ReflectedDefinition>]`.

## The map

One manifest resource, `FSharp.Interop.Dlr.CaptureMap`, format 2: an uncompressed header of
tab-separated lines (`DLRMAP 2`, then `F offset length file` per source file, then an empty
line), then each file's lines raw-deflated. Compressed per file, because the names that repeat
across members repeat within a file; the run time inflates a file's blob the first time one of
its blocks is looked up. A file's lines are its blocks: `B file line`, then optional sections,
`S fields` (`V field name ordinal`, `E field name ordinal element`, `U name ordinal element` for
a split tuple's element nothing keeps) and `S body`. Blocks are keyed by `Run`'s caller
information, the same file and line the run time has; under a path map (deterministic builds)
FCS applies the same mapping, so the keys still match.

## The build step

`FSharp.Interop.Dlr.Build.targets`, opted into with the DlrCompanion property, in any
configuration (a Debug build has no state machines, so no fields, but bodies):

- before CoreCompile, a nested build of the same project runs Compile with the compiler skipped
  (SkipCompilerExecution and ProvideCommandLineArgs; NonExistentFile forces CoreCompile to run and
  return its arguments; project references are not rebuilt);
- the companion writes the map under obj, incrementally (only when a source, the arguments or the
  tool changed);
- the map goes to fsc as a resource, so nothing is rewritten after compiling;
- the map and the companion setting are compile inputs, so a new map, or switching the companion
  off, recompiles.

## At run time

`CaptureMap.find` gives a block's fields, `CaptureMap.body` its body lines. The run time binds
through the fields only when they match the machine exactly: every entry a variable of the member,
its field one of the machine's, of a type that fits, and no field left out. Otherwise the block
keeps the strict behaviour. A map of another version is ignored whole. So a stale map, a map from
a different compiler, or a companion bug can make a block refuse, never bind a variable to the
wrong field. `DLR_CAPTURE_MAP_TRACE=1` reports per block whether it bound through the map, and why
not.

## Results

On this repository's `Tests` (Release, macOS): 2,207 lines with a block, 2,205 mapped; the two
left are the shapes the strict mode pins for refusal. With the map embedded the suite passes and
2,154 blocks bind exactly through it; the other blocks the trace reports are closures, which have
no state machine (a function-typed block applied on the spot, including a `let`-bound one applied
once). No map entry is ever rejected. The CI job `companion` builds `Tests` this way in Debug and
Release and fails on any rejected entry.

## Limits

- The recipe mirrors compiler internals: the stamp (read through reflection on FCS) and the
  naming rule. A compiler that changes either makes maps stop matching, which turns the feature
  off rather than making it wrong.
- The companion runs the FCS of the compiler that builds the project: the build passes the fsc.dll
  it compiles with (the SDK's, unless the project points elsewhere), and the companion loads
  FSharp.Compiler.Service and FSharp.Core from beside it in a load context of its own, so a new
  SDK's map describes the new compiler. If that fails (no FCS there, an API that moved), it
  writes an empty map: every block keeps the strict behaviour, and the build goes on.
- The project is checked twice per build: once by the companion, once by fsc.
- A block in an `inline` function is expanded into its callers and stays refused (DLR004), so
  SRTP witness fields never reach the map.
