module Tests.Index

open System.Collections.Generic
open Xunit
open FsUnit.Xunit
open FSharp.Interop.DLR

[<Fact>]
let ``get index on a dictionary`` () =
    let d = box (Dictionary<string, int>(dict [ "a", 1 ]))
    let k = "a"
    (dlr { return getIndex d k } : int) |> should equal 1

[<Fact>]
let ``get index on a CLR indexer`` () =
    let w = box (Widget())
    (dlr { return getIndex w 4 } : int) |> should equal 40

[<Fact>]
let ``set index on a dictionary`` () =
    let d = Dictionary<string, int>()
    let o = box d
    dlr { setIndex o "z" 26 }
    d.["z"] |> should equal 26

[<Fact>]
let ``multi-dimensional index splats a tuple`` () =
    let r = Recorder()
    let o = box r
    (dlr { return getIndex o (1, 2) } : int) |> should equal 2
    dlr { setIndex o (3, 4) "v" }
    List.ofSeq r.Log |> should equal [ "getIndex 1,2"; "setIndex 3,4=v" ]
