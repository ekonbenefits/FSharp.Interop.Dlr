[<ReflectedDefinition>]
module Tests.Mutables

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``a captured mutable can be assigned in the block`` () =
    let w = box (Widget())
    let mutable hits = 0
    dlr { hits <- hits + (w?Count : int) }
    hits |> should equal 3

[<Fact>]
let ``a captured mutable can be assigned inside for, while, try`` () =
    let w = box (Widget())
    let mutable hits = 0
    dlr {
        for _ in 1 .. 3 do
            let v: int = w?Count
            hits <- hits + v
    }
    hits |> should equal 9
    let mutable n = 0
    dlr {
        while n < 3 do
            n <- n + 1
            hits <- hits + (w?Count : int)
    }
    n |> should equal 3
    hits |> should equal 18
    dlr {
        try hits <- hits + (w?Missing : int)
        with :? RuntimeBinderException -> hits <- -1
    }
    hits |> should equal -1
    dlr {
        try hits <- w?Count
        finally hits <- hits * 10
    }
    hits |> should equal 30

[<Fact>]
let ``a let mutable inside the block works, including across loops and try`` () =
    let w = box (Widget())
    let total: int =
        dlr {
            let mutable acc = 0
            for _ in 1 .. 3 do
                acc <- acc + (w?Count : int)
            let mutable tries = 0
            try
                tries <- tries + 1
                acc <- acc + (w?Missing : int)
            with :? RuntimeBinderException -> tries <- tries + 1
            return acc * 10 + tries
        }
    total |> should equal 92

[<Fact>]
let ``a let mutable is not inlined as its initial value`` () =
    // normalize substitutes `let x = 0` bodies with the constant; a mutable must not be.
    let w = box (Widget())
    let r: int =
        dlr {
            let mutable x = 0
            x <- w?Count
            x <- x * 2
            return x
        }
    r |> should equal 6

[<Fact>]
let ``a let snapshot of a mutable keeps the value at that point`` () =
    // normalize inlines `let y = x` for a variable x — but not when x is mutable: a snapshot
    // must not read the current value later.
    let w = box (Widget())
    let mutable n = 1
    let r: string =
        dlr {
            let y = n
            n <- n + 1
            return w?Greet(string y, string n)
        }
    r |> should equal "1, 2"
    let inner: string =
        dlr {
            let mutable m = 1
            let snapshot = m
            m <- m + 1
            return w?Greet(string snapshot, string m)
        }
    inner |> should equal "1, 2"
