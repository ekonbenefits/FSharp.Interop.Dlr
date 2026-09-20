[<ReflectedDefinition>]
module Tests.Unary

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``unary operators`` () =
    let n, b = box 5, box true
    let negated: int = dlr { return Dlr.neg n }
    let inverted: bool = dlr { return Dlr.not b }
    let complemented: int = dlr { return Dlr.complement n }
    negated |> should equal -5
    inverted |> should equal false
    complemented |> should equal -6

[<Fact>]
let ``unary operators reach TryUnaryOperation`` () =
    let a: obj = Arith(4)
    let negated: Arith = dlr { return Dlr.neg a }
    negated.Value |> should equal -4

[<Fact>]
let ``unary on an unsupported operand raises RuntimeBinderException`` () =
    let s = box "text"
    (fun () -> (dlr { return Dlr.neg s } : obj) |> ignore) |> should throw typeof<RuntimeBinderException>
