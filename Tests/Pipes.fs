[<ReflectedDefinition>]
module Tests.Pipes

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

[<Fact>]
let ``get through a pipe`` () =
    let w = box (Widget())
    let n: int = dlr { return w |> Dlr.get "Count" }
    n |> should equal 3

[<Fact>]
let ``chained gets walk a dynamic graph`` () =
    let leaf = Fixtures.expando [ "Name", box "leaf" ]
    let root = box (Fixtures.expando [ "Child", box leaf ])
    let name: string = dlr { return root |> Dlr.get "Child" |> Dlr.get "Name" }
    name |> should equal "leaf"

[<Fact>]
let ``invoke through a pipe with tuple, unit and named args`` () =
    let w = Widget()
    let o = box w
    (dlr { return o |> Dlr.invoke "Add" (40, 2) } : int) |> should equal 42
    (dlr { return o |> Dlr.invoke "Greet" ("Hi", Dlr.named {| name = "Jay" |}) } : string) |> should equal "Hi, Jay"
    dlr { o |> Dlr.invoke "Touch" () }
    w.Touched |> should equal 1

[<Fact>]
let ``a piped get applied to arguments invokes, like ?`` () =
    let w = box (Widget())
    let r: string = dlr { return (w |> Dlr.get "Pick") 1 }
    r |> should equal "int"
    let s: string = dlr { return (Dlr.get "Greet" w) ("Yo", "you") }
    s |> should equal "Yo, you"

[<Fact>]
let ``set through a pipe ends the chain`` () =
    let w = Widget()
    let o = box w
    dlr { o |> Dlr.set "Count" 12 }
    w.Count |> should equal 12

[<Fact>]
let ``pipe chains mix invoke and get and convert at the end`` () =
    let w = box (Widget())
    let len: int64 = dlr { return w |> Dlr.invoke "Greet" ("Hi", "Jay") |> Dlr.get "Length" }
    len |> should equal 7L

[<Fact>]
let ``computed names through pipes`` () =
    let w = box (Widget())
    let read (name: string) : obj = dlr { return w |> Dlr.get name }
    read "Count" |> should equal (box 3)
    read "Name" |> should equal (box "widget")
    let call (name: string) : int = dlr { return w |> Dlr.invoke name (1, 2) }
    call "Add" |> should equal 3

[<Fact>]
let ``backward pipe and plain application work too`` () =
    let w = box (Widget())
    (dlr { return Dlr.get "Count" <| w } : int) |> should equal 3
    (dlr { return Dlr.get "Count" w } : int) |> should equal 3
