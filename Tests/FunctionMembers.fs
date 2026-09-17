/// Members holding F# function values (not delegates), which the C# binder alone cannot invoke.
[<ReflectedDefinition>]
module Tests.FunctionMembers

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

type Handlers = { OnValue: int -> int; OnPair: int -> int -> int }

let private bag () =
    Fixtures.expando [
        "Del", box (Func<int, int>(fun x -> x * 2))
        "Fn", box (fun (x: int) -> x * 2)
        "Curried", box (fun (a: int) (b: int) -> a + b)
        "Tupled", box (fun (a: int, b: int) -> a * b)
        "Thunk", box (fun () -> "ran")
        "Three", box (fun (a: int) (b: int) (c: int) -> a + b + c)
    ]

[<Fact>]
let ``an F# function in a dynamic member is invoked like a delegate would be`` () =
    let e = box (bag ())
    (dlr { return e?Del(21) } : int) |> should equal 42        // delegate: the binder's own path
    (dlr { return e?Fn(21) } : int) |> should equal 42         // FSharpFunc: the fallback
    (dlr { return e?Thunk() } : string) |> should equal "ran"

[<Fact>]
let ``curried and tupled functions both take a tuple call`` () =
    let e = box (bag ())
    (dlr { return e?Curried(1, 2) } : int) |> should equal 3
    (dlr { return e?Tupled(3, 4) } : int) |> should equal 12
    (dlr { return e?Three(1, 2, 3) } : int) |> should equal 6

[<Fact>]
let ``a curried function applied F# style goes through the optimized closure`` () =
    let e = box (bag ())
    let r: int = dlr { return (e?Curried : int -> int -> int) 10 5 }
    r |> should equal 15

[<Fact>]
let ``a function-typed member can be fetched and applied outside the block`` () =
    let e = box (bag ())
    let f: int -> int = dlr { return e?Fn }
    f 4 |> should equal 8
    let g: int -> int -> int = dlr { return e |> Dlr.get "Curried" }
    g 2 3 |> should equal 5

[<Fact>]
let ``Dlr.call applies an F# function target`` () =
    let e = box (bag ())
    (dlr { return (e |> Dlr.get "Fn") |> Dlr.call 21 } : int) |> should equal 42
    (dlr { return (e |> Dlr.get "Curried") |> Dlr.call (4, 5) } : int) |> should equal 9
    let thunk = box (fun () -> 7)
    (dlr { return thunk |> Dlr.call () } : int) |> should equal 7

[<Fact>]
let ``record fields holding functions on a CLR type`` () =
    let h = box { OnValue = (fun x -> x + 1); OnPair = (fun a b -> a * b) }
    (dlr { return h?OnValue(1) } : int) |> should equal 2
    (dlr { return h?OnPair(3, 4) } : int) |> should equal 12

[<Fact>]
let ``a wrong argument type still reports the binder's own error`` () =
    let e = box (bag ())
    let s = "not an int"
    (fun () -> (dlr { return e?Fn(s) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> (dlr { return e?Missing(1) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``computed names take the same fallback`` () =
    let e = box (bag ())
    let call (name: string) : int = dlr { return ((?) e name) (21) }
    call "Del" |> should equal 42
    call "Fn" |> should equal 42
