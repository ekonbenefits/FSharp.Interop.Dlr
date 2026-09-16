module Tests.Set

open Xunit
open FsUnit.Xunit
open FSharp.Interop.DLR

[<Fact>]
let ``set member on a CLR object`` () =
    let w = Widget()
    let o = box w
    dlr { o?Count <- 9 }
    w.Count |> should equal 9

[<Fact>]
let ``set member with closure value on expando`` () =
    let e = Fixtures.expando []
    let o = box e
    let v = "hello"
    dlr { o?Greeting <- v }
    (e :> System.Collections.Generic.IDictionary<string, obj>).["Greeting"] |> should equal (box "hello")

[<Fact>]
let ``set member reaches TrySetMember`` () =
    let r = Recorder()
    let o = box r
    dlr { o?X <- 1 }
    List.ofSeq r.Log |> should equal [ "set X=1" ]
