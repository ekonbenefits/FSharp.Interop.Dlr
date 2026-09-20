/// `Dlr.cast<T>` (C#'s explicit cast) and `Dlr.implicit` (the implicit conversion a `?` result gets on its own).
[<ReflectedDefinition>]
module Tests.Conversions

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``explicit cast truncates where the implicit conversion refuses`` () =
    let w: obj = Widget()
    let truncated: int = dlr { return Dlr.cast<int> w?Ratio }
    truncated |> should equal 2
    (fun () -> (dlr { return w?Ratio } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``explicit cast on a typed value and on an enum`` () =
    let d = 3.9
    let truncated = dlr { return Dlr.cast<int> d }
    let day = dlr { return Dlr.cast<System.DayOfWeek> 2 }
    truncated |> should equal 3
    day |> should equal System.DayOfWeek.Tuesday

[<Fact>]
let ``implicit conversion of a held value`` () =
    let x = box 5
    let widened: int64 = dlr { return Dlr.implicit x }
    widened |> should equal 5L
    let same: int = dlr { return Dlr.implicit x }
    same |> should equal 5
    let asObj: obj = dlr { return Dlr.implicit x }
    asObj |> should equal (box 5)

[<Fact>]
let ``implicit conversion uses op_Implicit and TryConvert`` () =
    let n = box 7
    let m: Meters = dlr { return Dlr.implicit n }
    m.Value |> should equal 7
    let a: obj = Arith(9)
    let text: string = dlr { return Dlr.implicit a }
    text |> should equal "9"

[<Fact>]
let ``implicit conversion refuses what a cast would allow`` () =
    let d = box 3.9
    (fun () -> (dlr { return Dlr.implicit d } : int) |> ignore) |> should throw typeof<RuntimeBinderException>
    let truncated: int = dlr { return Dlr.cast<int> d }
    truncated |> should equal 3
