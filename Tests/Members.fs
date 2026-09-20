[<ReflectedDefinition>]
module Tests.Members

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``get member converts to inferred type`` () =
    let w: obj = Widget()
    let count: int = dlr { return w?Count }
    count |> should equal 3

[<Fact>]
let ``a typed target is upcast and binds on its runtime type, as box would`` () =
    let w = Widget()
    let count: int = dlr { return w?Count }
    count |> should equal 3
    let b: Holders = Derived()
    let kind: string = dlr { return (Classifier())?Kind(b) }        // the target's static type does not matter...
    kind |> should equal "holders"                                 // ...the argument's does, as always
    let name (t: Widget) : string = dlr { return t?Name }
    name (Widget()) |> should equal "widget"

[<Fact>]
let ``get member as obj needs no conversion`` () =
    let w: obj = Widget()
    let name: obj = dlr { return w?Name }
    name |> should equal (box "widget")

[<Fact>]
let ``get member on expando`` () =
    let e: obj = Fixtures.expando [ "Answer", box 42 ]
    let answer: int = dlr { return e?Answer }
    answer |> should equal 42

[<Fact>]
let ``get member on DynamicObject reaches TryGetMember`` () =
    let r = Recorder()
    let o: obj = r
    let thing: string = dlr { return o?Thing }
    thing |> should equal "Thing"
    List.ofSeq r.Log |> should equal [ "get Thing" ]

[<Fact>]
let ``missing member raises RuntimeBinderException`` () =
    let w: obj = Widget()
    (fun () -> (dlr { return w?Nope } : obj) |> ignore)
    |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``get member with widening conversion`` () =
    let w: obj = Widget()
    let count: int64 = dlr { return w?Count }
    count |> should equal 3L

type Holder(w: obj) =
    member this.Count: int = dlr { return w?Count }
    member this.Self: string = dlr { return (w?Pick(this) : string) }

[<Fact>]
let ``dlr inside a class member captures this and fields`` () =
    let h = Holder(Widget())
    h.Count |> should equal 3
    h.Self |> should equal "obj"

[<Fact>]
let ``impossible conversion raises RuntimeBinderException`` () =
    let w: obj = Widget()
    (fun () -> (dlr { return w?Name } : int) |> ignore)
    |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``null target raises RuntimeBinderException`` () =
    let n: obj = null
    (fun () -> (dlr { return n?Count } : int) |> ignore)
    |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``binder context is the declaring type, so non-public members bind from inside it`` () =
    // F# `member private` is IL internal, so it also binds from elsewhere in this assembly;
    // String's private field shows the context does not open members of other assemblies.
    let w = Widget()
    w.PeekSecretFromOutside(w) |> should equal "hidden"
    (dlr { return (box w)?Secret } : string) |> should equal "hidden"
    let s = box "abc"
    (fun () -> (dlr { return s?_firstChar } : char) |> ignore) |> should throw typeof<RuntimeBinderException>
