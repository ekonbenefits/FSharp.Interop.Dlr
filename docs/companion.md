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
its name and its order among the member's bindings of that name, counted as the quotation lays
the member out: a `match` case that two paths reach (an or-pattern binding `x`) is bound at each
path, so it counts twice, and a `let rec` is its variables, then its body, then its definitions,
as F# puts them in the quotation. A value bound more than once (a guard's variable, bound for the
guard and again for its target; a handler's, for the filter and for the handler) is the binding in
scope at the block, found from the block's `Delay` in the unoptimized tree. Both trees are walked
after optimizing: the optimizer renames values the trees share (a match's input after the pattern
variable it stands for), and the reflected definition fsc pickles is the unoptimized expression with
those renames, so the unoptimized tree read after optimizing has the quotation's names. Each rule gives an exact answer
or none:

- a variable of the source is itself;
- an optimizer temporary (`x_0`, a split tuple's element) is that element of the source tuple
  whose value holds its range; a tuple parameter the compiler split counts as the quotation's one
  tuple only when its elements are compiler-generated (a user's own `(x_0, x_1)` is not a split);
- a closure's copy of a captured variable (a local function or lambda made a closure, or lifted:
  a new value with the enclosing function's range, a `let rec`'s being the whole construct, or a
  lambda parameter of the optimized member) is the one source variable of that name declared
  before it, never one matched by value: the function's range holds its own variables' definitions
  (#276); a captured `mutable` arrives as its ref cell;
- a copy let-bound to another value is that value's attribution;
- anything else: none, and the block is left out of the map. So is a block two of whose fields
  are attributed to one variable of a name the member binds more than once: one of them may be the
  other binding's (of a name bound once, both are that one, the optimizer keeping a copy beside it).

A block the compiler makes no state machine of runs `DlrRun.Closure` (in Release, a function-typed
block applied on the spot; [pipeline](pipeline.md)), and its container is the class of the
resumable-code delegate built over its inlined `Delay`. The pass takes that delegate's lambda's
free locals the same way (code generation's `GenDelegateExpr`, with the lambda's parameter `sm`
already a taken name), attributes them by the same rules, and writes them as the block's `closure`
section, beside its machine's `fields`: the run time reads the one its container's kind asks for.
A Debug build's closure is `Delay`'s own, which the pass does not describe, so it has no entry.

Nested blocks share a file and line, and only the outermost is ever looked up (an inner one
compiles as part of it), so per line the map keeps the block whose `dlr` comes first; in the
`closure` section, the block the others' closure calls are inside.

The `body` pass (`Bodies.run`) records each block's member body from the unoptimized tree, so a
block needs no `[<ReflectedDefinition>]`: `Discover` falls back to it when no reflected definition
has the block. `Companion/oracle.fsx` checks it, decoding each body and comparing it with the
member's reflected definition; its layout oracle lays each member out as the field pass does and
compares that, per name, with the quotation's variables (mutability and type, in order).

## The map

One manifest resource, `FSharp.Interop.Dlr.CaptureMap`, format 2: an uncompressed header of
tab-separated lines (`DLRMAP 2`, then `F offset length file` per source file, then an empty
line), then each file's lines raw-deflated. Compressed per file, because the names that repeat
across members repeat within a file; the run time inflates a file's blob the first time one of
its blocks is looked up. A file's lines are its blocks: `B file line`, then optional sections,
`S fields` (`V field name ordinal`, `E field name ordinal element`, and `N name count`: for
each name the entries use, how many bindings of it the companion counted in the member),
`S closure` (the same lines for the closure-path container, #276) and `S body`. A split tuple's element the optimizer kept nowhere has no line: the run time recovers
it from its definition (through an alias, a union case the member builds, a tuple literal) when
that is effect-free, and refuses otherwise. It never uses a placeholder, because the optimizer may
have dropped the element because it knew its value and used that directly (#278). So an element
nothing reads is refused too when its definition has an effect: nothing tells it apart from one
the optimizer folded. Without the map, the variables a recovered element reads count toward the
block's names, so one the block shadows is refused rather than bound by name (#279). Blocks are keyed by the outermost
`Delay`'s caller information, the same file and line the run time's probe finds; under a path map (deterministic builds)
FCS applies the same mapping, so the keys still match.

Little of the format is about `dlr { }`. It records, per source location, facts the compiler knows
and the run time does not: a member's body without `[<ReflectedDefinition>]`, and which compiled
field holds which source variable. A section the reader does not know is skipped, so a new kind of
fact is a new section, not a new version. What is this library's own: the resource name, the block
key (`Delay`'s caller information, through `DlrLocation`), the field pass taking only machines whose
code reaches `DlrLocation.Answer`, and the run-time checks. Another consumer (a resumable
computation expression inspecting its captures, a reader of code as data) would supply its own
key and checks, and could share the rest; it is kept that way on purpose, not yet split out.

## The build step

`FSharp.Interop.Dlr.Build.targets`, opted into with the DlrCompanion property, in any
configuration (a Debug build has no state machines, so no fields, but bodies):

- before CoreCompile, a nested build of the same project runs Compile with the compiler skipped
  (SkipCompilerExecution and ProvideCommandLineArgs; NonExistentFile forces CoreCompile to run and
  return its arguments; project references are not rebuilt);
- the companion writes the map under obj, incrementally (only when a source, the arguments or the
  tool changed);
- the map goes to fsc as a resource item (quoted, so a path with spaces works), so nothing is
  rewritten after compiling; an IDE's design-time build skips the step;
- the map and the companion setting are compile inputs, so a new map, or switching the companion
  off, recompiles;
- a tool that fails where it cannot catch it (killed for memory, exit 137) leaves an empty map,
  which reads as no map, and a stamp that makes the next build run it again: the build goes on.

## At run time

`CaptureMap.find` gives a block's fields, `CaptureMap.body` its body lines. The run time binds
through the fields only when all of these hold, and otherwise keeps the strict behaviour:

- every entry names a variable of the member, its field is one of the machine's, of a type that
  fits, and no field is left out;
- for each name the entries use, the map's binding count (`N`) is the quotation's: the companion
  and the quotation laid the member out alike, so `(name, order)` means the same variable to both;
- every variable the map gives a field is upstream of the block: one of its free variables, or a
  variable of their definitions in the member, transitively.

A map of another version, or a malformed one, is no map, never an exception. The checks matter
because the run time cannot tell two same-named variables of the same type apart by themselves:
a companion layout that disagreed with the quotation would bind the wrong one. That happened
before the count was checked (a `match` case two paths reach, before two same-named variables),
and the count now refuses it: with the companion's old layout the trace reads `'x' bound 3 times in
the map, 4 in the quotation`. So a stale map, a map from a different compiler, or a companion bug
makes a block refuse whenever it changes a name's count or points outside the block's upstream;
the safety otherwise rests on the companion mirroring the quotation's layout, which the CI job
and the regression tests in `Tests/Cache.fs` exercise. `DLR_CAPTURE_MAP_TRACE=1` reports per
block whether it bound through the map, and why not.

## Results

On this repository's `Tests` (Release, macOS): with the map embedded the suite passes and about
2,300 blocks bind exactly through it (the count varies a little with which blocks a run reaches),
the closures among them (a function-typed block applied on the spot, including a `let`-bound one
applied once) through their `closure` section; the two shapes the strict mode pins for refusal
have no entry. No map entry is rejected. Run on the closure path (`-p:DlrClosures=true`), every
block's container is its closure, and about 2,280 bind exactly through the `closure` section, none
rejected. The CI job `companion` builds `Tests` this way in Debug and Release, and on the closure
path in Release, and fails on any rejected entry; "0 rejected" shows the companion and the quotation agree on the suite's shapes,
not that they agree on every shape, which is what the run-time checks are for.

The differential corpus (#200, outside the repository) binds a name one way or another and reaches
it from a block one way or another. Each case has a plain twin, the same code with no block and the
call bound statically, so the compiler gives the expected value with no part of this library
involved. Its 51,808 cases of the first grammar run the same with the map, none wrong. The 14,084
cases of the forms added for #259 (an or-pattern, a guard, a `let rec` whose definition binds the
name or comes before it, a `try … with` handler, an active pattern, `function`) have none wrong
either, on SDK 10 and 11, and every map entry is used, with no rejection: 11,140 run the same,
against 5,104 without the map; the closure path and Debug (11's state machines too) run all of
them. The rest are refusals, most of them an active pattern the optimizer inlines (below). A block
in a `let rec` local function is a state machine whose captures arrive as the function's closure
copies, attributed as such (#276). On the corpus's two later subsets (#275's and #278's forms,
48,940 cases) the map gives 31,346 the same as their plain twins (30,338 before #276), none wrong,
and the same on the closure path with its `closure` section. An
or-pattern's arm holds its block once per alternative, and the copies are one block (#266, #271)
when every variable the block reads is a field, or recovered from a definition that is the same in
every copy up to its variables.

## Limits

- The recipe mirrors compiler internals: the stamp (read through reflection on FCS) and the
  naming rule. A compiler that changes either makes maps stop matching, which turns the feature
  off rather than making it wrong.
- The companion runs the FCS of the compiler that builds the project: the build passes the fsc.dll
  it compiles with (the SDK's, unless the project points elsewhere), and the companion loads
  FSharp.Compiler.Service and FSharp.Core from beside it in a load context of its own, so a new
  SDK's map describes the new compiler. If that fails (no FCS there, an API that moved), it
  writes an empty map: every block keeps the strict behaviour, and the build goes on.
- The tool targets .NET 10, so only the .NET 10 SDK or later builds it; once built, it runs under
  an older SDK's build too (8.0, 9.0: the .NET 10 runtime installed beside) against that compiler's
  own FCS (#261). Without a built tool the step is skipped with a message (the build has no map, and
  every block keeps the strict behaviour). Where FCS's API differs between versions the tool reads it
  by reflection: `FSharpChecker.Create` gains parameters (every optional one but
  `keepAssemblyContents` None), and `ApparentEnclosingEntity` is an option only from 10.0's FCS on.
  On the 11.0 SDK (rc.3) the corpus's 14,084 cases give the same result as on 10 (11,768 exact,
  none rejected, none wrong); on 9.0, `Tests` binds 2,223 blocks exactly, none rejected. Debug
  builds on 8.0, 9.0 and 11.0 are state machines too, but the field pass finds none in their
  Debug tree yet: those blocks have bodies and keep the strict behaviour.
- The project is checked twice per build: once by the companion, once by fsc.
- Two same-named variables of the same type, or two same-typed elements of one tuple, are told
  apart only by the companion's layout: the run-time checks catch a layout that changes a count or
  leaves the block's upstream, not one that swaps two such variables and keeps both.
- A block in an `inline` function is expanded into its callers and stays refused (DLR004), so
  SRTP witness fields never reach the map.
- Two guarded arms binding one name, the block in the second (`| Some x when x < 0 -> … | Some x
  when x > 0 -> dlr { … }`): the match compiler gives the machine's field a value with the first
  arm's range, which the field pass does not attribute between the arms. The block has no entry and
  keeps the strict behaviour (`Tests/Companion.fs` pins it).
- An active pattern the optimizer inlines away (`| Same x ->` with `Same` the identity) leaves the
  machine the match's input, which the map says, while the block reads the pattern's variable,
  whose definition is the pattern's call: the block refuses (it would run that call again), where
  the strict mode binds the field by name. Taking the variable the block reads instead is not done:
  it could bind wrongly where another variable of that name reaches the block through an inlined
  function.
- The companion checks the whole project at once, keeping both trees: a 66,000-member project (the
  corpus in one) ran a 16 GB machine out of memory, and the build then went on without a map.
