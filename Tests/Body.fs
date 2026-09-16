module Tests.Body

open Xunit
open FsUnit.Xunit
open FSharp.Interop.Dlr

[<Fact>]
let ``let and if in the body`` () =
    let w = box (Widget())
    let f (flag: bool) : string =
        dlr {
            let n: int = w?Count
            if flag && n > 2 then return w?Describe()
            else return w?Name
        }
    f true |> should equal "described"
    f false |> should equal "widget"

[<Fact>]
let ``sequential statements`` () =
    let w = Widget()
    let o = box w
    let n: int =
        dlr {
            o?Count <- 10
            o?Touch()
            return o?Count
        }
    n |> should equal 10
    w.Touched |> should equal 1

[<Fact>]
let ``closure values of several types`` () =
    let w = box (Widget())
    let a = 1
    let b = 2L
    let s = "x"
    let r: string = dlr { return w?Greet(s, string (a + int b)) }
    r |> should equal "x, 3"

[<Fact>]
let ``ordinary F# code mixes with dynamic calls`` () =
    let w = box (Widget())
    let items = [ 1; 2; 3 ]
    let total: int = dlr { return List.sum items + w?Count }
    total |> should equal 9

[<Fact>]
let ``nested dynamic call as obj dispatches on the runtime type`` () =
    let w = box (Widget())
    let r: string = dlr { return w?Pick(w?Count) }
    r |> should equal "int"

[<Fact>]
let ``nested dynamic call with typed intermediate`` () =
    let w = box (Widget())
    let r: string = dlr { return w?Pick(w?Count : int) }
    r |> should equal "int"
