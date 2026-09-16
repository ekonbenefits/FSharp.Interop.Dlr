[<ReflectedDefinition>]
module Tests.Invoke

open System
open Xunit
open FsUnit.Xunit
open FSharp.Interop.Dlr

[<Fact>]
let ``invoke with no args`` () =
    let w = box (Widget())
    let s: string = dlr { return w?Describe() }
    s |> should equal "described"

[<Fact>]
let ``invoke with one arg uses the static type for overload resolution`` () =
    let w = box (Widget())
    let n = 5
    let s = "five"
    (dlr { return w?Pick(n) } : string) |> should equal "int"
    (dlr { return w?Pick(s) } : string) |> should equal "string"

[<Fact>]
let ``invoke with an obj arg dispatches on the runtime type`` () =
    let w = box (Widget())
    let o = box 5
    (dlr { return w?Pick(o) } : string) |> should equal "int"

[<Fact>]
let ``invoke with tuple args`` () =
    let w = box (Widget())
    let a, b = 2, 40
    (dlr { return w?Add(a, b) } : int) |> should equal 42

[<Fact>]
let ``invoke with literal args`` () =
    let w = box (Widget())
    (dlr { return w?Add(1, 2) } : int) |> should equal 3

[<Fact>]
let ``named args reorder`` () =
    let w = box (Widget())
    let s: string = dlr { return w?Greet(Dlr.named {| name = "Jay"; greeting = "Hi" |}) }
    s |> should equal "Hi, Jay"

[<Fact>]
let ``named args mix with positional`` () =
    let w = box (Widget())
    let n = 10
    (dlr { return w?Bump(n, Dlr.named {| step = 5 |}) } : int) |> should equal 15
    (dlr { return w?Bump(n) } : int) |> should equal 11

[<Fact>]
let ``named args reach a DynamicObject by name`` () =
    let r = Recorder()
    let o = box r
    let s: string = dlr { return o?Call(1, Dlr.named {| second = 2 |}) }
    s |> should equal "1|2"
    List.ofSeq r.Log |> should equal [ "invoke Call(2 args; named second)" ]

[<Fact>]
let ``bare anonymous record is one positional arg`` () =
    let r = Recorder()
    let o = box r
    let _: string = dlr { return o?Call({| a = 1; b = 2 |}) }
    List.ofSeq r.Log |> should equal [ "invoke Call(1 args; named )" ]

[<Fact>]
let ``Dlr.named around a non-record is rejected`` () =
    let r = box (Recorder())
    let x = 1
    (fun () -> (dlr { return r?Call(Dlr.named x) } : string) |> ignore)
    |> should throw typeof<DlrTranslationException>

[<Fact>]
let ``unit result discards`` () =
    let w = Widget()
    let o = box w
    dlr { w?Touch() }
    dlr { do o?Touch() }
    w.Touched |> should equal 2

[<Fact>]
let ``invoke the target itself`` () =
    let f = box (Func<int, int>(fun x -> x * 2))
    (dlr { return (!?f)(21) } : int) |> should equal 42

[<Fact>]
let ``delegate arg passes through`` () =
    let w = box (Widget())
    let f = Func<int, int>(fun x -> x * 2)
    (dlr { return w?Run(f) } : int) |> should equal 42

[<Fact>]
let ``invoke the target with a unit result`` () =
    let mutable hits = 0
    let f = box (Action<int>(fun x -> hits <- hits + x))
    dlr { (!?f)(5) }
    hits |> should equal 5
