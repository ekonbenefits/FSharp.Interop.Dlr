[<ReflectedDefinition>]
module Tests.ComputedNames

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``get member by a computed name`` () =
    let w = box (Widget())
    let read (name: string) : obj = dlr { return (?) w name }
    read "Count" |> should equal (box 3)
    read "Name" |> should equal (box "widget")

[<Fact>]
let ``computed name converts to the inferred type`` () =
    let w = box (Widget())
    let count (name: string) : int = dlr { return (?) w name }
    count "Count" |> should equal 3

[<Fact>]
let ``invoke by a computed name with typed and named args`` () =
    let w = box (Widget())
    let call (name: string) (n: int) : string = dlr { return ((?) w name) (n) }
    call "Pick" 1 |> should equal "int"
    let greet (name: string) : string = dlr { return ((?) w name) ("Hi", Dlr.named {| name = "Jay" |}) }
    greet "Greet" |> should equal "Hi, Jay"

[<Fact>]
let ``set member by a computed name`` () =
    let w = Widget()
    let o = box w
    let set (name: string) (v: int) = dlr { (?<-) o name v }
    set "Count" 11
    w.Count |> should equal 11

[<Fact>]
let ``computed names on a DynamicObject and Expando`` () =
    let r = Recorder()
    let o = box r
    let get (name: string) : string = dlr { return (?) o name }
    get "Alpha" |> should equal "Alpha"
    get "Beta" |> should equal "Beta"
    List.ofSeq r.Log |> should equal [ "get Alpha"; "get Beta" ]
    let e = box (Fixtures.expando [ "x", box 1; "y", box 2 ])
    let sum: int = dlr { return (?) e "x" + ((?) e (string 'y') : int) }
    sum |> should equal 3

[<Fact>]
let ``each distinct name compiles once and the site stays one cache entry`` () =
    let w = box (Widget())
    let read (name: string) : obj = dlr { return (?) w name }
    let before = DlrCache.count ()
    for _ in 1 .. 100 do
        read "Count" |> ignore
        read "Name" |> ignore
    DlrCache.count () |> should equal (before + 1)

[<Fact>]
let ``computed name that does not exist raises RuntimeBinderException`` () =
    let w = box (Widget())
    let read (name: string) : obj = dlr { return (?) w name }
    (fun () -> read "Nope" |> ignore) |> should throw typeof<RuntimeBinderException>
