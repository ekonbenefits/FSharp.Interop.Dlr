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
    (dlr { return d |> Dlr.item k } : int) |> should equal 1

[<Fact>]
let ``get index on a CLR indexer`` () =
    let w = box (Widget())
    (dlr { return w |> Dlr.item 4 } : int) |> should equal 40

[<Fact>]
let ``set index on a dictionary`` () =
    let d = Dictionary<string, int>()
    let o = box d
    dlr { o |> Dlr.setItem "z" 26 }
    d.["z"] |> should equal 26

[<Fact>]
let ``multi-dimensional index splats a tuple`` () =
    let r = Recorder()
    let o = box r
    (dlr { return o |> Dlr.item (1, 2) } : int) |> should equal 2
    dlr { o |> Dlr.setItem (3, 4) "v" }
    List.ofSeq r.Log |> should equal [ "getIndex 1,2"; "setIndex 3,4=v" ]

[<Fact>]
let ``closure indexes`` () =
    let d = Dictionary<string, int>()
    let o = box d
    let k = "q"
    dlr { o |> Dlr.setItem k 7 }
    (dlr { return o |> Dlr.item k } : int) |> should equal 7

[<Fact>]
let ``get index result converts to the inferred type`` () =
    let d = box (Dictionary<string, int>(dict [ "a", 1 ]))
    let v: int64 = dlr { return d |> Dlr.item "a" }
    v |> should equal 1L

[<Fact>]
let ``three and four indexes, and more`` () =
    let r = Recorder()
    let o = box r
    (dlr { return o |> Dlr.item (1, 2, 3) } : int) |> should equal 3
    (dlr { return o |> Dlr.item (1, 2, 3, 4) } : int) |> should equal 4
    dlr { o |> Dlr.setItem (1, 2, 3) "a" }
    dlr { o |> Dlr.setItem (1, 2, 3, 4) "b" }
    (dlr { return o |> Dlr.item (1, 2, 3, 4, 5) } : int) |> should equal 5
    List.ofSeq r.Log |> should equal [ "getIndex 1,2,3"; "getIndex 1,2,3,4"; "setIndex 1,2,3=a"; "setIndex 1,2,3,4=b"; "getIndex 1,2,3,4,5" ]
