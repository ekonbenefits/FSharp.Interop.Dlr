[<ReflectedDefinition>]
module Tests.Cache

open Xunit
open FsUnit.Xunit
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
let ``generic enclosing function is rejected clearly`` () =
    (fun () -> genericPick (Widget()) 1 |> ignore) |> should throw typeof<DlrTranslationException>
