[<ReflectedDefinition>]
module Tests.Unary

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``explicit cast truncates where the implicit conversion refuses`` () =
    let w = box (Widget())
    (dlr { return Dlr.cast<int> w?Ratio } : int) |> should equal 2
    (fun () -> (dlr { return w?Ratio } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``explicit cast on a typed value and on an enum`` () =
    let d = 3.9
    (dlr { return Dlr.cast<int> d }) |> should equal 3
    (dlr { return Dlr.cast<System.DayOfWeek> 2 }) |> should equal System.DayOfWeek.Tuesday

[<Fact>]
let ``unary operators`` () =
    let n, b = box 5, box true
    (dlr { return Dlr.neg n } : int) |> should equal -5
    (dlr { return Dlr.not b } : bool) |> should equal false
    (dlr { return Dlr.complement n } : int) |> should equal -6

[<Fact>]
let ``unary operators reach TryUnaryOperation`` () =
    let a = box (Arith(4))
    (dlr { return Dlr.neg a } : Arith).Value |> should equal -4

[<Fact>]
let ``unary on an unsupported operand raises RuntimeBinderException`` () =
    let s = box "text"
    (fun () -> (dlr { return Dlr.neg s } : obj) |> ignore) |> should throw typeof<RuntimeBinderException>
