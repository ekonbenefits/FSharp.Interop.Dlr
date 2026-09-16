[<ReflectedDefinition>]
module Tests.TryBlocks

open System
open Xunit
open FsUnit.Xunit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``a failed bind can be caught inside the block`` () =
    let w = box (Widget())
    let r: string =
        dlr {
            try return w?Missing
            with :? RuntimeBinderException -> return "no such member"
        }
    r |> should equal "no such member"

[<Fact>]
let ``unmatched exceptions propagate`` () =
    let w = box (Widget())
    (fun () ->
        (dlr {
            try return (w?Missing : string)
            with :? ArgumentException -> return "wrong handler"
        }) |> ignore)
    |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``handler can use the exception and dynamic calls`` () =
    let w = box (Widget())
    let r: string =
        dlr {
            try return w?Nope
            with e -> return w?Greet(e.GetType().Name, w?Name)
        }
    r |> should equal "RuntimeBinderException, widget"

[<Fact>]
let ``finally runs on success and on failure`` () =
    let w = Widget()
    let o = box w
    let ok: int =
        dlr {
            try return o?Count
            finally o?Touch()
        }
    ok |> should equal 3
    (fun () ->
        (dlr {
            try return (o?Missing : int)
            finally o?Touch()
        }) |> ignore)
    |> should throw typeof<RuntimeBinderException>
    w.Touched |> should equal 2

[<Fact>]
let ``try inside a loop keeps going`` () =
    let w = box (Widget())
    let names = [ "Count"; "Missing"; "Name" ]
    let acc = ResizeArray<string>()
    dlr {
        for n in names do
            try acc.Add(string (Dlr.idx w).[n])
            with :? RuntimeBinderException -> acc.Add("?")
    }
    // Widget has no string indexer, so every item fails the same way: the point is the loop survives.
    List.ofSeq acc |> should equal [ "?"; "?"; "?" ]
