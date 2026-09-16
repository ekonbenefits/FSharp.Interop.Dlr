[<ReflectedDefinition>]
module Tests.Operators

open Xunit
open FsUnit.Xunit
open FSharp.Interop.Dlr

[<Fact>]
let ``add ints`` () =
    let a, b = box 1, box 2
    (dlr { return a ?+? b } : int) |> should equal 3

[<Fact>]
let ``add strings concatenates`` () =
    let a, b = box "foo", box "bar"
    (dlr { return a ?+? b } : string) |> should equal "foobar"

[<Fact>]
let ``arithmetic on mixed numeric types`` () =
    let a, b = box 5, box 2.5
    (dlr { return a ?*? b } : float) |> should equal 12.5
    (dlr { return a ?-? b } : float) |> should equal 2.5
    (dlr { return a ?/? b } : float) |> should equal 2.0
    (dlr { return a ?%? (box 3) } : int) |> should equal 2

[<Fact>]
let ``comparisons return bool`` () =
    let a, b = box 1, box 2
    dlr { return a ?<? b } |> should equal true
    dlr { return a ?>? b } |> should equal false
    dlr { return a ?=? b } |> should equal false
    dlr { return a ?<>? b } |> should equal true
    dlr { return a ?<=? a } |> should equal true
    dlr { return a ?>=? b } |> should equal false

[<Fact>]
let ``bitwise and shifts`` () =
    let a, b = box 6, box 3
    (dlr { return a ?&&&? b } : int) |> should equal 2
    (dlr { return a ?|||? b } : int) |> should equal 7
    (dlr { return a ?^^^? b } : int) |> should equal 5
    (dlr { return a ?<<<? (box 1) } : int) |> should equal 12
    (dlr { return a ?>>>? (box 1) } : int) |> should equal 3

[<Fact>]
let ``operators on unboxed values use the static type`` () =
    let a = 40
    (dlr { return a ?+? 2 } : int) |> should equal 42
