[<ReflectedDefinition>]
module Tests.Cache

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

// These tests share the global cache, so they are careful to only assert on deltas.

[<Fact>]
let ``same site compiles once for many calls`` () =
    let w = box (Widget())
    let f (i: int) : int = dlr { return w?Add(i, 1) }
    f 0 |> ignore
    let before = DlrCache.count ()
    for i in 1 .. 1000 do f i |> should equal (i + 1)
    DlrCache.count () |> should equal before

[<Fact>]
let ``different closure values reuse the delegate`` () =
    let f (o: obj) : string = dlr { return o?Pick(1) }
    f (Widget()) |> should equal "int"
    let before = DlrCache.count ()
    f (Widget()) |> should equal "int"
    DlrCache.count () |> should equal before

[<Fact>]
let ``two blocks on one line are detected`` () =
    let w = box (Widget())
    let go () =
        // fsharpanalyzer: ignore-line-next DLR003
        let a: int = dlr { return w?Count } in let b: string = dlr { return w?Name } in (a, b)
    (fun () -> go () |> ignore) |> should throw typeof<DlrTranslationException>

[<Fact>]
let ``clear forces recompilation`` () =
    let w = box (Widget())
    let f () : int = dlr { return w?Count }
    f () |> should equal 3
    DlrCache.clear ()
    f () |> should equal 3

[<Fact>]
let ``mutable capture reads the current value`` () =
    let w = box (Widget())
    let mutable n = 1
    let f () : int = dlr { return w?Add(n, 1) }
    f () |> should equal 2
    n <- 10
    f () |> should equal 11

let genericPick (w: obj) (x: 'a) : string = dlr { return w?Pick(x) }

[<Fact>]
let ``generic enclosing function: each instantiation is its own site with concrete types`` () =
    let w = Widget()
    let before = DlrCache.count ()
    genericPick w 1 |> should equal "int"
    genericPick w "s" |> should equal "string"
    genericPick w 2.5 |> should equal "obj"
    DlrCache.count () |> should equal (before + 3)
    genericPick w 7 |> should equal "int"
    genericPick w "t" |> should equal "string"
    DlrCache.count () |> should equal (before + 3)

[<Fact>]
let ``values the optimizer inlines instead of capturing still resolve`` () =
    // In Release, F# inlines constant locals and local functions into the closure instead of
    // capturing them; the reflected body still names them, so they resolve from their let binding.
    let w = box (Widget())
    let five = 5
    let nothing: obj = null
    let greeting () = "Hi"
    (dlr { return w?Add(five, five) } : int) |> should equal 10
    (dlr { return w?Text(nothing) } : string) |> should equal "string"
    (dlr { return w?Greet(greeting (), "you") } : string) |> should equal "Hi, you"
    // A lambda applied on the spot is beta-reduced the same way: its parameter is the argument.
    (fun (k: int) -> (dlr { return w?Add(k, 1) } : int)) 41 |> should equal 42
    (fun (a: int) (b: string) -> (dlr { return w?Greet(b, string a) } : string)) 7 "Hi" |> should equal "Hi, 7"

[<Fact>]
let ``clear then a call recompiles`` () =
    let w = box (Widget())
    let read () : int = dlr { return w?Count }
    read () |> should equal 3
    DlrCache.clear ()
    let before = DlrCache.count ()
    read () |> should equal 3
    DlrCache.count () - before |> should equal 1          // recompiled, not served from a stale typed entry

[<Fact>]
let ``captured variables named like the state machine's own fields still resolve`` () =
    // The compiled block's struct has `Data` and `ResumptionPoint` fields of its own, which the
    // translator must not resolve a variable to. `Data`: the closure gets a second field of that
    // name (of the local's type). `ResumptionPoint`: the optimizer inlines the literal, so there is
    // no field and the variable resolves from the enclosing member, not to the machine's counter
    // (which would read 0). An `int` of that name the optimizer keeps is a compiler error in
    // Release, as in task { }; a `let mutable` is an FSharpRef field and clashes with nothing.
    let Data = box (Widget())
    let ResumptionPoint = 2
    (dlr { return Data?Add(ResumptionPoint, 1) } : int) |> should equal 3
    let mutable ResumptionPoint = 5
    dlr { ResumptionPoint <- Data?Add(ResumptionPoint, 1) }
    ResumptionPoint |> should equal 6
