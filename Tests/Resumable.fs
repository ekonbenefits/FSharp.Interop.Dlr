/// The compiler's resumable-code behaviour the library depends on (#180): which path each build
/// takes, and shapes the compiler lowers differently from a block in a function.
module Tests.Resumable

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

/// A block bound as a module-level value: the compiler takes the non-resumable path for it
/// without FS3511 (dotnet/fsharp#18672), and it runs once, at module initialisation.
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
let ``a block bound as a module-level value, the attribute on the binding`` () =
    ModuleValueOnBinding.count |> should equal 3

[<Fact>]
let ``a block bound as a module-level value, the attribute on the module`` () =
    ModuleValueOnModule.count |> should equal 3

[<Fact>]
let ``a small function holding a block, called from another module`` () =
    Small.count (box (Widget())) |> should equal 3
    Small.count Fixtures.plainWidget |> should equal 3

[<ReflectedDefinition>]
[<Fact>]
let ``a captured tuple formatted with sprintf "%A %s"`` () =
    // dotnet/fsharp#20675: inside task { }, this format gives a closure typed string.
    let t = (1, 2)
    let w = box (Widget())
    let s: string = dlr { return sprintf "%A %s" t (w?Name : string) }
    s |> should equal (sprintf "%A %s" t "widget")
