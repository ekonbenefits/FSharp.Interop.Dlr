[<ReflectedDefinition>]
module Tests.CompoundAssign

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

[<Fact>]
let ``addAssign and subtractAssign on a CLR event add and remove a handler`` () =
    let c = Clicker()
    let o = box c
    let seen = ResizeArray<int>()
    let handler = Handler<int>(fun _ n -> seen.Add n)
    dlr { o |> Dlr.addAssign "Clicked" handler }
    c.Raise 1
    dlr { o |> Dlr.subtractAssign "Clicked" handler }
    c.Raise 2
    List.ofSeq seen |> should equal [ 1 ]

[<Fact>]
let ``addAssign on a numeric property reads, adds and writes back`` () =
    let w = Widget()
    let o = box w
    dlr { o |> Dlr.addAssign "Total" 5 }
    w.Total |> should equal 15
    dlr { o |> Dlr.subtractAssign "Total" 3 }
    w.Total |> should equal 12

[<Fact>]
let ``addAssign of an int literal to a byte member uses the constant conversion`` () =
    let w = Widget()
    let o = box w
    dlr { o |> Dlr.addAssign "Small" 5 }
    w.Small |> should equal 255uy

[<Fact>]
let ``addAssign on a string concatenates, like C#`` () =
    let w = Widget()
    let o = box w
    dlr { o |> Dlr.addAssign "Label" "b" }
    w.Label |> should equal "ab"

[<Fact>]
let ``addAssign on an Expando delegate member combines delegates`` () =
    let calls = ResizeArray<string>()
    let e = Fixtures.expando [ "Handler", box (Action(fun () -> calls.Add "first")) ]
    let o = box e
    let second = Action(fun () -> calls.Add "second")
    dlr { o |> Dlr.addAssign "Handler" second }
    (dlr { return o?Handler } : Action).Invoke()
    dlr { o |> Dlr.subtractAssign "Handler" second }
    (dlr { return o?Handler } : Action).Invoke()
    List.ofSeq calls |> should equal [ "first"; "second"; "first" ]

[<Fact>]
let ``addAssign on an Expando number`` () =
    let e = Fixtures.expando [ "Count", box 1 ]
    let o = box e
    for _ in 1 .. 3 do
        dlr { o |> Dlr.addAssign "Count" 2 }
    (dlr { return o?Count } : int) |> should equal 7

[<Fact>]
let ``addAssign with a computed name`` () =
    let w = Widget()
    let o = box w
    let bump (name: string) (by: int) = dlr { o |> Dlr.addAssign name by }
    bump "Total" 1
    bump "Count" 1
    (w.Total, w.Count) |> should equal (11, 4)

[<Fact>]
let ``addAssign and subtractAssign on a meta-object's event, with a delegate`` () =
    let host = EventHost()
    let o = box host
    let seen = ResizeArray<int>()
    let handler = Action<int>(fun n -> seen.Add n)
    dlr { o |> Dlr.addAssign "Changed" handler }
    host.Raise 1
    dlr { o |> Dlr.subtractAssign "Changed" handler }
    host.Raise 2
    List.ofSeq seen |> should equal [ 1 ]

[<Fact>]
let ``an F# function added to a meta-object's event arrives as the delegate of its signature`` () =
    // COM's bound event (Tests/Com.fs) takes delegates only, as this one does. A new delegate is
    // made per conversion, so subtractAssign with the same function cannot find it — pinned
    // below: keep a delegate to unsubscribe, as with a C# lambda.
    let host = EventHost()
    let o = box host
    let seen = ResizeArray<int>()
    let handler = fun (n: int) -> seen.Add n
    dlr { o |> Dlr.addAssign "Changed" handler }
    host.Raise 3
    dlr { o |> Dlr.subtractAssign "Changed" handler }
    host.Raise 6
    List.ofSeq seen |> should equal [ 3; 6 ]            // still subscribed
    host.HandlerCount |> should equal 1

[<Fact>]
let ``an F# function added to a CLR event converts to the event's delegate type`` () =
    let c = Clicker()
    let o = box c
    let seen = ResizeArray<int>()
    let handler = fun (_: obj) (n: int) -> seen.Add n
    dlr { o |> Dlr.addAssign "Clicked" handler }
    c.Raise 4
    dlr { o |> Dlr.subtractAssign "Clicked" handler }   // a new delegate: not the one added
    c.Raise 5
    List.ofSeq seen |> should equal [ 4; 5 ]

[<Fact>]
let ``one addAssign site alternates functions and delegates on a meta-object's event`` () =
    let host = EventHost()
    let o = box host
    let seen = ResizeArray<string>()
    let handlers: obj list = [ box (fun (n: int) -> seen.Add(sprintf "f%d" n)); box (Action<int>(fun n -> seen.Add(sprintf "d%d" n))) ]
    for h in handlers do
        dlr { o |> Dlr.addAssign "Changed" h }
    host.Raise 5
    List.ofSeq seen |> should equal [ "f5"; "d5" ]
