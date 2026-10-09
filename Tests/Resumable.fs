/// The compiler's resumable-code behaviour the library depends on (#180): which path each build
/// takes, and shapes the compiler lowers differently from a block in a function.
module Tests.Resumable

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

/// A block bound as a module-level value. dotnet/fsharp#18672 reports such a value taking the
/// non-resumable path without FS3511; for this builder Release still builds its state machine.
/// It runs once, at module initialisation.
module ModuleValueOnBinding =
    [<ReflectedDefinition>]
    let count: int = dlr { return Fixtures.plainWidget?Count }

[<ReflectedDefinition>]
module ModuleValueOnModule =
    let count: int = dlr { return Fixtures.plainWidget?Count }

/// A small function holding a block: the kind the Release optimizer may inline into its callers.
module Small =
    [<ReflectedDefinition>]
    let count (o: obj) : int = dlr { return o?Count }

/// A function-typed block bound with `let` (#217): applied once, fully or partially, the optimizer
/// inlines the binding into its one use, which is the block applied on the spot, so the closure
/// path; used twice it stays a value, so a state machine. The block's result type does not matter.
[<ReflectedDefinition>]
module LetBound =
    let once (o: obj) : int = let onceUsed: int -> int -> int = dlr { return o?Add } in onceUsed 1 2
    let twice (o: obj) : int = let twiceUsed: int -> int -> int = dlr { return o?Add } in twiceUsed 1 2 + twiceUsed 3 4
    let onceUnit (o: obj) : unit = let onceTouch: unit -> unit = dlr { return o?Touch } in onceTouch ()
    let partial (o: obj) : int -> int = let partialUsed: int -> int -> int = dlr { return o?Add } in partialUsed 1

/// The struct state machines the compiler built for blocks in this assembly.
let private machines () =
    Reflection.Assembly.GetExecutingAssembly().GetTypes()
    |> Array.filter (fun t ->
        t.IsValueType
        && t.GetInterfaces() |> Array.exists (fun i -> i.Name.StartsWith "IResumableStateMachine" && i.GetGenericArguments().[0].Name.StartsWith "DlrData"))

[<Fact>]
let ``Debug builds every block on the closure path, Release on state machines`` () =
    // SDK 10.0.400 builds no state machine in Debug. dotnet/fsharp#20469 keeps resumable code
    // static in Debug "whenever possible"; if a later SDK does that for blocks, this fails, and the
    // closure path is then covered only by the shapes that take it in every build (#180).
#if DEBUG
    machines () |> should haveLength 0
#else
    (machines ()).Length |> should be (greaterThan 0)
#endif

[<Fact>]
let ``a function-typed block bound with let and applied once takes the closure path in Release`` () =
    let w: obj = Widget()
    LetBound.once w |> should equal 3
    LetBound.twice w |> should equal 10
    LetBound.onceUnit w
    LetBound.partial w 2 |> should equal 3
#if !DEBUG
    let named (binding: string) = machines () |> Array.exists (fun t -> t.Name.StartsWith(binding + "@"))
    named "onceUsed" |> should equal false        // inlined into its one application: on the spot
    named "onceTouch" |> should equal false
    named "twiceUsed" |> should equal true
    named "partialUsed" |> should equal false     // applied once, partially: on the spot too
#endif

[<Fact>]
let ``a block bound as a module-level value, the attribute on the binding`` () =
    ModuleValueOnBinding.count |> should equal 3

[<Fact>]
let ``a block bound as a module-level value, the attribute on the module`` () =
    ModuleValueOnModule.count |> should equal 3

[<Fact>]
let ``a small function holding a block, called from another module`` () =
    Small.count (box (Widget())) |> should equal 3
    Small.count Fixtures.plainWidget |> should equal 3

[<Fact>]
let ``a small function holding a block, called from another assembly`` () =
    Tests.FSharpLib.Blocks.count (box (Widget())) |> should equal 3
    Tests.FSharpLib.Blocks.count Fixtures.plainWidget |> should equal 3

[<ReflectedDefinition>]
[<Fact>]
let ``a captured tuple formatted with sprintf "%A %s"`` () =
    // dotnet/fsharp#20675: inside task { }, this format gives a closure typed string.
    let t = (1, 2)
    let w = box (Widget())
    let s: string = dlr { return sprintf "%A %s" t (w?Name : string) }
    s |> should equal (sprintf "%A %s" t "widget")
