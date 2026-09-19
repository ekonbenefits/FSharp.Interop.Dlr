[<ReflectedDefinition>]
module Tests.Set

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

[<Fact>]
let ``set member on a CLR object`` () =
    let w = Widget()
    let o: obj = w
    dlr { o?Count <- 9 }
    w.Count |> should equal 9

[<Fact>]
let ``set member with closure value on expando`` () =
    let e = Fixtures.expando []
    let o: obj = e
    let greeting = "hello"
    dlr { o?Greeting <- greeting }
    (e :> System.Collections.Generic.IDictionary<string, obj>).["Greeting"] |> should equal (box "hello")

[<Fact>]
let ``set member reaches TrySetMember`` () =
    let r = Recorder()
    let o: obj = r
    dlr { o?X <- 1 }
    List.ofSeq r.Log |> should equal [ "set X=1" ]
