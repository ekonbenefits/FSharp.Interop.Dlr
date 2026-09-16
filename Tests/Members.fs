[<ReflectedDefinition>]
module Tests.Members

open System
open Xunit
open FsUnit.Xunit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``get member converts to inferred type`` () =
    let w = box (Widget())
    let n: int = dlr { return w?Count }
    n |> should equal 3

[<Fact>]
let ``get member as obj needs no conversion`` () =
    let w = box (Widget())
    let n: obj = dlr { return w?Name }
    n |> should equal (box "widget")

[<Fact>]
let ``get member on expando`` () =
    let e = box (Fixtures.expando [ "Answer", box 42 ])
    let n: int = dlr { return e?Answer }
    n |> should equal 42

[<Fact>]
let ``get member on DynamicObject reaches TryGetMember`` () =
    let r = Recorder()
    let o = box r
    let s: string = dlr { return o?Thing }
    s |> should equal "Thing"
    List.ofSeq r.Log |> should equal [ "get Thing" ]

[<Fact>]
let ``missing member raises RuntimeBinderException`` () =
    let w = box (Widget())
    (fun () -> (dlr { return w?Nope } : obj) |> ignore)
    |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``get member with widening conversion`` () =
    let w = box (Widget())
    let n: int64 = dlr { return w?Count }
    n |> should equal 3L

type Holder(w: obj) =
    member this.Count: int = dlr { return w?Count }
    member this.Self: string = dlr { return (w?Pick(this) : string) }

[<Fact>]
let ``dlr inside a class member captures this and fields`` () =
    let h = Holder(Widget())
    h.Count |> should equal 3
    h.Self |> should equal "obj"
