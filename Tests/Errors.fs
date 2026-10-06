[<ReflectedDefinition>]
module Tests.Errors

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

[<Fact>]
let ``a stack trace names the block by its file and line`` () =
    // The compiled body has no line numbers; its frame's name says which block it is. Browser-wasm
    // interprets the tree, with frames of the interpreter's own.
    if string Runtime.InteropServices.RuntimeInformation.OSArchitecture = "Wasm" then
        raise (AnyUnit.IgnoreException "the expression interpreter has no frame per block")
    let w = box (Widget())
    let line, ex = __LINE__, (try (dlr { return w?NoSuchMember() } : int) |> ignore; null with e -> e)
    ex.StackTrace |> should haveSubstring (sprintf "dlr@Errors.fs:%s(" line)

[<Fact>]
let ``a block's nested parts are named after it, numbered when repeated`` () =
    if string Runtime.InteropServices.RuntimeInformation.OSArchitecture = "Wasm" then
        raise (AnyUnit.IgnoreException "the expression interpreter has no frame per block")
    let w = box (Widget())
    let line = int __LINE__ + 3
    let ex =
        try
            dlr {
                for _ in [ 1 ] do ()
                for _ in [ 1 ] do w?NoSuchMember() }
            null
        with e -> e
    // The second loop's body, inside the block: both frames carry the block's file and line.
    ex.StackTrace |> should haveSubstring (sprintf "dlr@Errors.fs:%d-for-2(" line)
    ex.StackTrace |> should haveSubstring (sprintf "dlr@Errors.fs:%d(" line)

[<Fact>]
let ``a loop inside a loop is numbered in source order`` () =
    if string Runtime.InteropServices.RuntimeInformation.OSArchitecture = "Wasm" then
        raise (AnyUnit.IgnoreException "the expression interpreter has no frame per block")
    let w = box (Widget())
    let line = int __LINE__ + 3
    let ex =
        try
            dlr {
                for _ in [ 1 ] do
                    for _ in [ 1 ] do w?NoSuchMember() }
            null
        with e -> e
    // Innermost first: the inner loop (-for-2), then the outer (-for), then the block.
    let at (frame: string) = ex.StackTrace.IndexOf(sprintf "dlr@Errors.fs:%d%s(" line frame)
    at "-for-2" |> should be (greaterThanOrEqualTo 0)
    (at "-for-2" < at "-for" && at "-for" < at "") |> should equal true

// This test calls every marker outside a block on purpose; the analyzer would report each one.
// fsharpanalyzer: ignore-region-start DLR002
[<Fact>]
let ``every operator and marker throws outside dlr`` () =
    let w = box (Widget())
    let a, b = box 1, box 2
    let outside (f: unit -> unit) = f |> should throw typeof<InvalidOperationException>
    outside (fun () -> (w?Count : int) |> ignore)
    outside (fun () -> w?Count <- 1)
    outside (fun () -> (Dlr.apply 1 w : int) |> ignore)
    outside (fun () -> (Dlr.call w : int) |> ignore)
    outside (fun () -> Dlr.named {| a = 1 |} |> ignore)
    outside (fun () -> Dlr.namedOf [ "a", box 1 ] |> ignore)
    outside (fun () -> Dlr.argsOf [ box 1 ] |> ignore)
    outside (fun () -> Dlr.out |> ignore)
    outside (fun () -> Dlr.outAs<int> () |> ignore)
    outside (fun () -> Dlr.ref 1 |> ignore)
    outside (fun () -> (Dlr.item 0 w : int) |> ignore)
    outside (fun () -> Dlr.setItem 0 1 w)
    outside (fun () -> Dlr.typeArgs<int>() |> ignore)
    outside (fun () -> Dlr.typeArgs<int, int>() |> ignore)
    outside (fun () -> Dlr.typeArgs<int, int, int>() |> ignore)
    outside (fun () -> Dlr.typeArgs<int, int, int, int>() |> ignore)
    outside (fun () -> Dlr.typeArgsOf [ typeof<int> ] |> ignore)
    outside (fun () -> (Dlr.new'<Widget>() : Widget) |> ignore)
    outside (fun () -> Dlr.Static<Widget>.Overloads |> ignore)
    outside (fun () -> (Dlr.new'<Widget>(1) : Widget) |> ignore)
    outside (fun () -> (Dlr.new'<Widget>(1, 2) : Widget) |> ignore)
    outside (fun () -> (Dlr.new'<Widget>(1, 2, 3) : Widget) |> ignore)
    outside (fun () -> (Dlr.new'<Widget>(1, 2, 3, 4) : Widget) |> ignore)
    outside (fun () -> (Dlr.new'<Widget>(1, 2, 3, 4, 5) : Widget) |> ignore)
    outside (fun () -> (Dlr.new'<Widget>(1, 2, 3, 4, 5, 6) : Widget) |> ignore)
    outside (fun () -> (Dlr.new'<Widget>(1, 2, 3, 4, 5, 6, 7) : Widget) |> ignore)
    outside (fun () -> (Dlr.new'<Widget>(1, 2, 3, 4, 5, 6, 7, 8) : Widget) |> ignore)
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
let ``a loop inside a lambda, once an unsupported node, translates`` () =
    // A raw loop node has no expression-tree form; it runs through the same delegates as the
    // block's own loops (#162). No shape the corpus (Shapes.fs) covers is unsupported any more.
    let w = box (Widget())
    let picked: string =
        dlr {
            let f () = for i in 1 .. 2 do ignore i
            return w?Pick(f)
        }
    picked |> should equal "obj"

[<Fact>]
let ``two blocks on one line are detected`` () =
    let w = box (Widget())
    let go () =
        // fsharpanalyzer: ignore-line-next DLR003
        let a: int = dlr { return w?Count } in let b: string = dlr { return w?Name } in (a, b)
    (fun () -> go () |> ignore) |> should throw typeof<DlrTranslationException>
