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
let ``a member read as a function type is a curried invoker of it`` () =
    // Whatever the member is: an F# function, a delegate, a method. Applied when fully applied.
    let e = box (bag ())
    let f: int -> int = dlr { return e?Fn }
    f 4 |> should equal 8
    let g: int -> int -> int = dlr { return e |> Dlr.get "Curried" }
    g 2 3 |> should equal 5
    let d: int -> int = dlr { return e?Del }
    d 21 |> should equal 42
    let w = box (Widget())
    let add: int -> int -> int = dlr { return w?Add }
    add 40 2 |> should equal 42
    let addTupled: int * int -> int = dlr { return w?Add }
    addTupled (40, 2) |> should equal 42

[<Fact>]
let ``a curried invoker supports partial application and converts its result`` () =
    let w = box (Widget())
    let add: int -> int -> int64 = dlr { return w?Add }
    let add40 = add 40
    add40 2 |> should equal 42L
    let pick: obj -> string = dlr { return w?Pick }
    pick (box 1) |> should equal "int"

[<Fact>]
let ``unit -> R reads a property or invokes a parameterless method`` () =
    let w = Widget()
    let o = box w
    let count: unit -> int = dlr { return o?Count }
    count () |> should equal 3
    w.Count <- 5
    count () |> should equal 5                       // deferred: reads on each call
    let describe: unit -> string = dlr { return o?Describe }
    describe () |> should equal "described"

[<Fact>]
let ``a computed name read as a function type`` () =
    let w = box (Widget())
    let bind (name: string) : int -> int -> int = dlr { return (?) w name }
    (bind "Add") 1 2 |> should equal 3

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
