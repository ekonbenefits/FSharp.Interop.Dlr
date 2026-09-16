[<ReflectedDefinition>]
module Tests.Index

open System.Collections.Generic
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

[<Fact>]
let ``get index on a dictionary`` () =
    let d = box (Dictionary<string, int>(dict [ "a", 1 ]))
    let k = "a"
    (dlr { return (Dlr.idx d).[k] } : int) |> should equal 1

[<Fact>]
let ``get index on a CLR indexer`` () =
    let w = box (Widget())
    (dlr { return (Dlr.idx w).[4] } : int) |> should equal 40

[<Fact>]
let ``set index on a dictionary`` () =
    let d = Dictionary<string, int>()
    let o = box d
    dlr { (Dlr.idx o).["z"] <- 26 }
    d.["z"] |> should equal 26

[<Fact>]
let ``multi-dimensional index splats a tuple`` () =
    let r = Recorder()
    let o = box r
    (dlr { return (Dlr.idx o).[1, 2] } : int) |> should equal 2
    dlr { (Dlr.idx o).[3, 4] <- "v" }
    List.ofSeq r.Log |> should equal [ "getIndex 1,2"; "setIndex 3,4=v" ]

[<Fact>]
let ``F# 6 index syntax and closure indexes`` () =
    let d = Dictionary<string, int>()
    let o = box d
    let k = "q"
    dlr { (Dlr.idx o)[k] <- 7 }
    (dlr { return (Dlr.idx o)[k] } : int) |> should equal 7

[<Fact>]
let ``get index result converts to the inferred type`` () =
    let d = box (Dictionary<string, int>(dict [ "a", 1 ]))
    let v: int64 = dlr { return (Dlr.idx d).["a"] }
    v |> should equal 1L

[<Fact>]
let ``three and four indexes`` () =
    let r = Recorder()
    let o = box r
    (dlr { return (Dlr.idx o).[1, 2, 3] } : int) |> should equal 3
    (dlr { return (Dlr.idx o).[1, 2, 3, 4] } : int) |> should equal 4
    dlr { (Dlr.idx o).[1, 2, 3] <- "a" }
    dlr { (Dlr.idx o).[1, 2, 3, 4] <- "b" }
    List.ofSeq r.Log |> should equal [ "getIndex 1,2,3"; "getIndex 1,2,3,4"; "setIndex 1,2,3=a"; "setIndex 1,2,3,4=b" ]
