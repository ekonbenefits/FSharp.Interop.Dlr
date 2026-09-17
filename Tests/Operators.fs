[<ReflectedDefinition>]
module Tests.Operators

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
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

[<Fact>]
let ``equality on records, unions, tuples, lists and options is structural`` () =
    // C#'s binder would compare these by reference (no op_Equality): F# `=` semantics instead.
    let a, b, c = box { X = 1; Y = 2 }, box { X = 1; Y = 2 }, box { X = 2; Y = 2 }
    dlr { return a ?=? b } |> should equal true
    dlr { return a ?=? c } |> should equal false
    dlr { return a ?<>? c } |> should equal true
    dlr { return box (Rect(1, 2)) ?=? box (Rect(1, 2)) } |> should equal true
    dlr { return box (Circle 1) ?=? box (Rect(1, 2)) } |> should equal false
    dlr { return box (1, "x") ?=? box (1, "x") } |> should equal true
    dlr { return box [ 1; 2 ] ?=? box [ 1; 2 ] } |> should equal true
    dlr { return box (Some 1) ?=? box (Some 1) } |> should equal true
    dlr { return box (Set.ofList [ 1; 2 ]) ?=? box (Set.ofList [ 2; 1 ]) } |> should equal true
    dlr { return box { SX = 1; SY = 1 } ?=? box { SX = 1; SY = 1 } } |> should equal true   // C#: "cannot be applied"
    dlr { return box (Opaque "t") ?=? box (Opaque "t") } |> should equal true

[<Fact>]
let ``ordering on structural types uses F# comparison`` () =
    let a, b = box { X = 1; Y = 2 }, box { X = 1; Y = 3 }
    dlr { return a ?<? b } |> should equal true
    dlr { return a ?<=? b } |> should equal true
    dlr { return a ?>? b } |> should equal false
    dlr { return a ?>=? a } |> should equal true
    dlr { return box (Circle 5) ?<? box (Rect(0, 0)) } |> should equal true      // case order
    dlr { return box [ 1; 2 ] ?<? box [ 1; 3 ] } |> should equal true
    dlr { return box (Some 1) ?>? box None } |> should equal true
    dlr { return box "a" ?<? box "b" } |> should equal true                        // C# has no string ordering; F#'s ordinal
    // Not comparable: the failure is F#'s, not C#'s "operator cannot be applied".
    (fun () -> dlr { return box (Opaque "a") ?<? box (Opaque "b") } |> ignore) |> should throw typeof<ArgumentException>

[<Fact>]
let ``null against a structural type compares as F# does`` () =
    let p, n = box { X = 1; Y = 2 }, box null
    dlr { return p ?=? n } |> should equal false
    dlr { return n ?=? p } |> should equal false
    dlr { return n ?<>? p } |> should equal true
    dlr { return box None ?=? n } |> should equal true      // None is null
    dlr { return n ?=? box "s" } |> should equal false     // C#'s own string == null

[<Fact>]
let ``types with their own CLR operator or C# semantics keep them`` () =
    // op_Equality declared: C# binds it (and its result), not structural equality.
    dlr { return box (Money 1) ?=? box (Money 1) } |> should equal true
    dlr { return box (Money 1) ?<>? box (Money 2) } |> should equal true
    // Primitives, enums, strings: C# as before (including numeric widening across types).
    dlr { return box 1 ?=? box 1L } |> should equal true
    dlr { return box DayOfWeek.Monday ?<? box DayOfWeek.Friday } |> should equal true
    dlr { return box "x" ?=? box "x" } |> should equal true
    // A dynamic object answers for itself.
    dlr { return box (EqualsAnything()) ?=? box { X = 1; Y = 2 } } |> should equal true

[<Fact>]
let ``one equality site serves primitives and structural types alternately`` () =
    let pairs = [ box 1, box 1; box { X = 1; Y = 1 }, box { X = 1; Y = 1 }; box "a", box "b"; box [ 1 ], box [ 2 ]; box 2, box 3 ]
    let results = ResizeArray<bool>()
    dlr {
        for (l, r) in pairs do
            results.Add(l ?=? r)
    }
    List.ofSeq results |> should equal [ true; true; false; false; false ]
