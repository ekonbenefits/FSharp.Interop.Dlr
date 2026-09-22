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
let ``TryInvoke handles Dlr.apply on a DynamicObject`` () =
    let a: obj = Arith(1)
    let invoked: string = dlr { return a |> Dlr.apply (1, 2) }
    invoked |> should equal "invoked with 2 args"

[<Fact>]
let ``TryBinaryOperation handles the operators`` () =
    let a, b = box (Arith(6)), box (Arith(7))
    let sum: Arith = dlr { return a ?+? b }
    let product: Arith = dlr { return a ?*? (box 2) }
    sum.Value |> should equal 13
    product.Value |> should equal 12
    dlr { return a ?=? b } |> should equal false
    dlr { return a ?<? b } |> should equal true

[<Fact>]
let ``TryConvert handles the inferred result type`` () =
    let e: obj = Fixtures.expando [ "A", box (Arith(42)) ]
    let asInt: int = dlr { return e?A }
    let asString: string = dlr { return e?A }
    asInt |> should equal 42
    asString |> should equal "42"

[<Fact>]
let ``unsupported TryBinaryOperation raises RuntimeBinderException`` () =
    let a, b = box (Arith(1)), box (Arith(2))
    (fun () -> (dlr { return a ?-? b } : obj) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``TryGetMember returning false raises RuntimeBinderException`` () =
    let a: obj = Arith(1)
    let value: int = dlr { return a?Value }
    value |> should equal 1
    (fun () -> (dlr { return a?Missing } : obj) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``Expando invokes a stored delegate member`` () =
    let e: obj = Fixtures.expando [ "Double", box (Func<int, int>(fun x -> x * 2)) ]
    let doubled: int = dlr { return e?Double(21) }
    doubled |> should equal 42

[<Fact>]
let ``Expando members set then read in one block`` () =
    let e: obj = ExpandoObject()
    let total: int =
        dlr {
            e?X <- 20
            e?Y <- 22
            return (e?X : int) + (e?Y : int)
        }
    total |> should equal 42

[<Fact>]
let ``IDynamicMetaObjectProvider implemented directly`` () =
    let bag = Bag()
    let o: obj = bag
    dlr { o?Name <- "jay" }
    let name: string = dlr { return o?Name }
    name |> should equal "jay"
    bag.Data.["Name"] |> should equal (box "jay")

[<Fact>]
let ``an F# function handed to a dynamic object arrives as the delegate of its signature`` () =
    // A script host or a DynamicObject understands delegates, never an FSharpFunc: the seam converts
    // by the function's own type — `int -> int` a Func<int, int>, `unit -> unit` an Action.
    let c: obj = Caller()
    let inc: int = dlr { return c?run(fun (x: int) -> x + 1) }
    inc |> should equal 21
    let twice: int = dlr { return c?twice((fun (x: int) -> x * 3), 2) }
    twice |> should equal 18
    let hits = ResizeArray<int>()
    dlr { c?run(fun (x: int) -> hits.Add x) }
    List.ofSeq hits |> should equal [ 20 ]
    let fired = ref false
    dlr { c?onEvent <- fun () -> fired.Value <- true }
    (c :?> Caller).Handler.DynamicInvoke() |> ignore
    fired.Value |> should equal true
    dlr { c |> Dlr.setItem 0 (fun (x: int) -> x + 100) }                      // set by index too
    (c :?> Caller).Handler.DynamicInvoke(box 1) |> should equal (box 101)
    // One site, the second slot a value then a function: the rule is per argument-type combination.
    let kinds (g: obj) : string = dlr { return c?kinds((fun (x: int) -> x * 3), g) }
    kinds (box 2) |> should equal "delegate,Int32"
    kinds (box (fun (s: string) -> s.Length)) |> should equal "delegate,delegate"
    kinds (box 2) |> should equal "delegate,Int32"
    // A CLR target is untouched: the parameter type drives the conversion there, as before.
    let w: obj = Widget()
    let ran: int = dlr { return w?Run(fun (x: int) -> x * 2) }
    ran |> should equal 42

[<Fact>]
let ``mixed runtime types through one site`` () =
    // The same block sees an Expando, a CLR object and a DynamicObject: the site's rule cache grows.
    let f (o: obj) : int = dlr { return o?Count }
    f (Fixtures.expando [ "Count", box 1 ]) |> should equal 1
    f (Widget()) |> should equal 3
    let d = Dictionary<string, int>()
    d.["a"] <- 0
    f d |> should equal 1
