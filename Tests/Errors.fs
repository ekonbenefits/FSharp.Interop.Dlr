[<ReflectedDefinition>]
module Tests.Errors

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

// This test calls every marker outside a block on purpose; the analyzer would report each one.
// fsharpanalyzer: ignore-region-start DLR002
[<Fact>]
let ``every operator and marker throws outside dlr`` () =
    let w = box (Widget())
    let a, b = box 1, box 2
    let outside (f: unit -> unit) = f |> should throw typeof<InvalidOperationException>
    outside (fun () -> (w?Count : int) |> ignore)
    outside (fun () -> w?Count <- 1)
    outside (fun () -> (Dlr.call 1 w : int) |> ignore)
    outside (fun () -> Dlr.named {| a = 1 |} |> ignore)
    outside (fun () -> (Dlr.idx w : Indexed<int>) |> ignore)
    outside (fun () -> Dlr.typeArgs<int>() |> ignore)
    outside (fun () -> Dlr.typeArgs<int, int>() |> ignore)
    outside (fun () -> Dlr.typeArgs<int, int, int>() |> ignore)
    outside (fun () -> Dlr.typeArgs<int, int, int, int>() |> ignore)
    outside (fun () -> Dlr.cast<int> a |> ignore)
    outside (fun () -> (Dlr.implicit a : int) |> ignore)
    outside (fun () -> (Dlr.get "Count" w : int) |> ignore)
    outside (fun () -> Dlr.set "Count" 1 w)
    outside (fun () -> Dlr.addAssign "Count" 1 w)
    outside (fun () -> Dlr.subtractAssign "Count" 1 w)
    outside (fun () -> (Dlr.invoke "Add" (1, 2) w : int) |> ignore)
    outside (fun () -> (Dlr.neg a : int) |> ignore)
    outside (fun () -> (Dlr.not a : bool) |> ignore)
    outside (fun () -> (Dlr.complement a : int) |> ignore)
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

// fsharpanalyzer: ignore-region-end
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
        // A loop inside a lambda is a raw loop node, which has no expression-tree form
        // (loops at block level go through the builder and are translated).
        dlr {
            let f () = for i in 1 .. 2 do ignore i
            return (w?Pick(f) : string)
        } |> ignore)
    |> should throw typeof<DlrTranslationException>

