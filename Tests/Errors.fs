[<ReflectedDefinition>]
module Tests.Errors

open System
open Xunit
open FsUnit.Xunit
open FSharp.Interop.Dlr

[<Fact>]
let ``every operator and marker throws outside dlr`` () =
    let w = box (Widget())
    let a, b = box 1, box 2
    let outside (f: unit -> unit) = f |> should throw typeof<InvalidOperationException>
    outside (fun () -> (w?Count : int) |> ignore)
    outside (fun () -> w?Count <- 1)
    outside (fun () -> ((!?w) : int) |> ignore)
    outside (fun () -> Dlr.named {| a = 1 |} |> ignore)
    outside (fun () -> (Dlr.idx w : Indexed<int>) |> ignore)
    outside (fun () -> (a ?%? b : int) |> ignore)
    outside (fun () -> (a ?*? b : int) |> ignore)
    outside (fun () -> (a ?+? b : int) |> ignore)
    outside (fun () -> (a ?-? b : int) |> ignore)
    outside (fun () -> (a ?/? b : int) |> ignore)
    outside (fun () -> (a ?&&&? b : int) |> ignore)
    outside (fun () -> (a ?|||? b : int) |> ignore)
    outside (fun () -> (a ?^^^? b : int) |> ignore)
    outside (fun () -> (a ?<<<? b : int) |> ignore)
    outside (fun () -> (a ?>>>? b : int) |> ignore)
    outside (fun () -> a ?<=? b |> ignore)
    outside (fun () -> a ?<>? b |> ignore)
    outside (fun () -> a ?<? b |> ignore)
    outside (fun () -> a ?=? b |> ignore)
    outside (fun () -> a ?>? b |> ignore)
    outside (fun () -> a ?>=? b |> ignore)

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

