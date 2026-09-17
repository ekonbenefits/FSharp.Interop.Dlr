[<ReflectedDefinition>]
module Tests.DynamicObjects

open System
open System.Collections.Generic
open System.Dynamic
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``TryInvoke handles Dlr.call on a DynamicObject`` () =
    let a = box (Arith(1))
    (dlr { return a |> Dlr.call (1, 2) } : string) |> should equal "invoked with 2 args"

[<Fact>]
let ``TryBinaryOperation handles the operators`` () =
    let a, b = box (Arith(6)), box (Arith(7))
    (dlr { return a ?+? b } : Arith).Value |> should equal 13
    (dlr { return a ?*? (box 2) } : Arith).Value |> should equal 12
    dlr { return a ?=? b } |> should equal false
    dlr { return a ?<? b } |> should equal true

[<Fact>]
let ``TryConvert handles the inferred result type`` () =
    let e = box (Fixtures.expando [ "A", box (Arith(42)) ])
    (dlr { return e?A } : int) |> should equal 42
    (dlr { return e?A } : string) |> should equal "42"

[<Fact>]
let ``unsupported TryBinaryOperation raises RuntimeBinderException`` () =
    let a, b = box (Arith(1)), box (Arith(2))
    (fun () -> (dlr { return a ?-? b } : obj) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``TryGetMember returning false raises RuntimeBinderException`` () =
    let a = box (Arith(1))
    (dlr { return a?Value } : int) |> should equal 1
    (fun () -> (dlr { return a?Missing } : obj) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``Expando invokes a stored delegate member`` () =
    let e = box (Fixtures.expando [ "Double", box (Func<int, int>(fun x -> x * 2)) ])
    (dlr { return e?Double(21) } : int) |> should equal 42

[<Fact>]
let ``Expando members set then read in one block`` () =
    let e = box (ExpandoObject())
    let n: int =
        dlr {
            e?X <- 20
            e?Y <- 22
            return (e?X : int) + (e?Y : int)
        }
    n |> should equal 42

[<Fact>]
let ``IDynamicMetaObjectProvider implemented directly`` () =
    let bag = Bag()
    let o = box bag
    dlr { o?Name <- "jay" }
    (dlr { return o?Name } : string) |> should equal "jay"
    bag.Data.["Name"] |> should equal (box "jay")

[<Fact>]
let ``mixed runtime types through one site`` () =
    // The same block sees an Expando, a CLR object and a DynamicObject: the site's rule cache grows.
    let f (o: obj) : int = dlr { return o?Count }
    f (Fixtures.expando [ "Count", box 1 ]) |> should equal 1
    f (Widget()) |> should equal 3
    let d = Dictionary<string, int>()
    d.["a"] <- 0
    f d |> should equal 1
