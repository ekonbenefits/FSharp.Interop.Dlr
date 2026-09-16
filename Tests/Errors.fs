module Tests.Errors

open System
open Xunit
open FsUnit.Xunit
open FSharp.Interop.DLR

[<Fact>]
let ``bare ? outside dlr throws`` () =
    let w = box (Widget())
    (fun () -> (w?Count : int) |> ignore) |> should throw typeof<InvalidOperationException>

[<Fact>]
let ``bare ?<- outside dlr throws`` () =
    let w = box (Widget())
    (fun () -> w?Count <- 1) |> should throw typeof<InvalidOperationException>

[<Fact>]
let ``bare operator outside dlr throws`` () =
    (fun () -> (box 1 ?+? box 2 : int) |> ignore) |> should throw typeof<InvalidOperationException>

[<Fact>]
let ``dynamic member as first-class function is rejected`` () =
    let w = box (Widget())
    (fun () ->
        let f: int -> string = dlr { return w?Pick }
        f 1 |> ignore)
    |> should throw typeof<DlrTranslationException>

[<Fact>]
let ``unsupported quotation node is reported`` () =
    let w = box (Widget())
    (fun () ->
        dlr {
            let rec fact n = if n <= 1 then 1 else n * fact (n - 1)
            return fact (w?Count : int)
        } |> ignore)
    |> should throw typeof<DlrTranslationException>
