[<ReflectedDefinition>]
module Tests.Index

open System.Collections.Generic
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

[<Fact>]
let ``get index on a dictionary`` () =
    let d: obj = Dictionary<string, int>(dict [ "a", 1 ])
    let key = "a"
    let value: int = dlr { return d |> Dlr.item key }
    value |> should equal 1

[<Fact>]
let ``get index on a CLR indexer`` () =
    let w: obj = Widget()
    let fourth: int = dlr { return w |> Dlr.item 4 }
    fourth |> should equal 40

[<Fact>]
let ``set index on a dictionary`` () =
    let d = Dictionary<string, int>()
    let o: obj = d
    dlr { o |> Dlr.setItem "z" 26 }
    d.["z"] |> should equal 26

[<Fact>]
let ``multi-dimensional index splats a tuple`` () =
    let r = Recorder()
    let o: obj = r
    let cell: int = dlr { return o |> Dlr.item (1, 2) }
    cell |> should equal 2
    dlr { o |> Dlr.setItem (3, 4) "v" }
    List.ofSeq r.Log |> should equal [ "getIndex 1,2"; "setIndex 3,4=v" ]

[<Fact>]
let ``closure indexes`` () =
    let d = Dictionary<string, int>()
    let o: obj = d
    let key = "q"
    dlr { o |> Dlr.setItem key 7 }
    let stored: int = dlr { return o |> Dlr.item key }
    stored |> should equal 7

[<Fact>]
let ``get index result converts to the inferred type`` () =
    let d: obj = Dictionary<string, int>(dict [ "a", 1 ])
    let widened: int64 = dlr { return d |> Dlr.item "a" }
    widened |> should equal 1L

[<Fact>]
let ``three and four indexes, and more`` () =
    let r = Recorder()
    let o: obj = r
    (dlr { return o |> Dlr.item (1, 2, 3) } : int) |> should equal 3
    (dlr { return o |> Dlr.item (1, 2, 3, 4) } : int) |> should equal 4
    dlr { o |> Dlr.setItem (1, 2, 3) "a" }
    dlr { o |> Dlr.setItem (1, 2, 3, 4) "b" }
    (dlr { return o |> Dlr.item (1, 2, 3, 4, 5) } : int) |> should equal 5
    List.ofSeq r.Log |> should equal [ "getIndex 1,2,3"; "getIndex 1,2,3,4"; "setIndex 1,2,3=a"; "setIndex 1,2,3,4=b"; "getIndex 1,2,3,4,5" ]
