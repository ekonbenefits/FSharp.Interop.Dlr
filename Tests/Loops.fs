[<ReflectedDefinition>]
module Tests.Loops

open Xunit
open FsUnit.Xunit
open FSharp.Interop.Dlr

[<Fact>]
let ``for over a list calls the site per item`` () =
    let r = Recorder()
    let o = box r
    let items = [ 1; 2; 3 ]
    dlr {
        for i in items do
            o?Push(i)
    }
    List.ofSeq r.Log |> should equal [ "invoke Push(1 args; named )"; "invoke Push(1 args; named )"; "invoke Push(1 args; named )" ]

[<Fact>]
let ``for then return combines`` () =
    let w = Widget()
    let o = box w
    let n: int =
        dlr {
            for _ in [| 1; 2 |] do
                o?Touch()
            return o?Touched
        }
    n |> should equal 2

[<Fact>]
let ``while with a ref counter`` () =
    let w = Widget()
    let o = box w
    let i = ref 0
    dlr {
        while i.Value < 4 do
            o?Touch()
            i.Value <- i.Value + 1
    }
    w.Touched |> should equal 4

[<Fact>]
let ``loop body can branch and nest dynamic calls`` () =
    let w = box (Widget())
    let acc = ResizeArray<string>()
    dlr {
        for i in 1 .. 4 do
            if i % 2 = 0 then acc.Add(w?Pick(i))
            else acc.Add(w?Pick(string i))
    }
    List.ofSeq acc |> should equal [ "string"; "int"; "string"; "int" ]

[<Fact>]
let ``loop iterations reuse one site`` () =
    let w = box (Widget())
    let before = DlrCache.count ()
    let total = ref 0
    dlr {
        for i in 1 .. 1000 do
            total.Value <- total.Value + (w?Add(i, 0) : int)
    }
    total.Value |> should equal 500500
    DlrCache.count () |> should equal (before + 1)
