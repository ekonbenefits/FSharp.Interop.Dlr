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
        "Four", box (fun (a: int) (b: int) (c: int) (d: int) -> a * 1000 + b * 100 + c * 10 + d)
        "FourTupled", box (fun (a: int, b: int, c: int, d: int) -> a + b + c + d)
        "Do", box (fun () -> ())
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
let ``four arguments, curried and tupled, as a member call and through Dlr.call`` () =
    let e = box (bag ())
    (dlr { return e?Four(1, 2, 3, 4) } : int) |> should equal 1234
    (dlr { return e?FourTupled(1, 2, 3, 4) } : int) |> should equal 10
    (dlr { return (e |> Dlr.get "Four") |> Dlr.call (4, 3, 2, 1) } : int) |> should equal 4321
    (dlr { return (e |> Dlr.get "FourTupled") |> Dlr.call (1, 1, 1, 1) } : int) |> should equal 4
    let f: int -> int -> int -> int -> int = dlr { return e?Four }
    f 5 6 7 8 |> should equal 5678

[<Fact>]
let ``a unit-returning F# function member called as a statement`` () =
    let hits = ResizeArray<string>()
    let e = box (Fixtures.expando [ "Log", box (fun (s: string) -> hits.Add s); "Tick", box (fun () -> hits.Add "tick") ])
    dlr { e?Log("a") }
    dlr { e?Tick() }
    let tick = box (fun () -> hits.Add "called")
    dlr { tick |> Dlr.call () }
    let w = Widget()
    let o = box w
    dlr { o?Touch() }                        // a void method still binds with the result discarded
    List.ofSeq hits |> should equal [ "a"; "tick"; "called" ]
    w.Touched |> should equal 1

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

[<Fact>]
let ``a discarded result still applies the function that is actually there`` () =
    // The statement form infers `int -> unit`; the member is `int -> int`. The shape comes from the
    // function, not the call, so it is applied and its value dropped, as C# drops a discarded result.
    let calls = ResizeArray<int>()
    let e = box (Fixtures.expando [ "Fn", box (fun (x: int) -> calls.Add x; x * 2) ])
    dlr { e?Fn(21) }
    let f = box (fun (x: int) -> calls.Add x; x)
    dlr { f |> Dlr.call 7 }
    List.ofSeq calls |> should equal [ 21; 7 ]

[<Fact>]
let ``a CLR member declared obj holding a function is applied by its runtime type`` () =
    let h = box (Holders())
    (dlr { return h?AsObj(2) } : int) |> should equal 6
    (dlr { return h?AsFunction(2) } : int) |> should equal 3

[<Fact>]
let ``a CLR delegate property read as unit -> R is invoked, not returned`` () =
    let h = box (Holders())
    let d: unit -> int = dlr { return h?AsDelegate }
    d () |> should equal 9
    let f: int -> int = dlr { return h?AsObj }
    f 5 |> should equal 15

[<Fact>]
let ``an indexed property is left to C#, which binds it as an index, not a member call`` () =
    let h = box (Holders())
    (dlr { return h |> Dlr.item 2 } : int) |> should equal 20
    // As in C#, `d.Item(4)` is not how an indexer is called; the error is the binder's, not a
    // bind-time failure building a property read without its index.
    (fun () -> (dlr { return h?Item(4) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``non-public F# function members and optional-parameter methods bind from an allowed context`` () =
    // F# `member private` is IL internal: the whole assembly is an allowed context, like C#'s binder.
    let h = Holders()
#if DLRQ
    // dlrq { } has no enclosing member: its context is obj, and IL-internal members do not bind.
    (fun () -> h.Reveal(h) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> h.RevealOptional(h) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> (dlr { return (box h)?Hidden(3) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>
#else
    h.Reveal(h) |> should equal 9
    h.RevealOptional(h) |> should equal 101
    (dlr { return (box h)?Hidden(3) } : int) |> should equal 2
#endif

[<Fact>]
let ``an obj-typed argument matches a function shape by its runtime type`` () =
    let e = box (Fixtures.expando [ "Fn", box (fun (x: int) -> x * 2); "Two", box (fun (a: int) (b: string) -> sprintf "%d%s" a b) ])
    let n = box 21
    let a, b = box 4, box "x"
    (dlr { return e?Fn(n) } : int) |> should equal 42
    (dlr { return e?Two(a, b) } : string) |> should equal "4x"
    let f = box (fun (x: int) -> x + 1)
    (dlr { return f |> Dlr.call n } : int) |> should equal 22

[<Fact>]
let ``unit -> unit binds a void method, an Action and a unit function`` () =
    let w = Widget()
    let touch: unit -> unit = dlr { return (box w)?Touch }
    touch ()
    let hits = ref 0
    let e = box (Fixtures.expando [ "Act", box (Action(fun () -> hits.Value <- hits.Value + 1)); "Fn", box (fun () -> hits.Value <- hits.Value + 10) ])
    let act: unit -> unit = dlr { return e?Act }
    let fn: unit -> unit = dlr { return e?Fn }
    act ()
    fn ()
    let log: string -> unit = dlr { return e?Fn2 }   // bound before the member exists...
    (fun () -> log "x") |> should throw typeof<RuntimeBinderException>
    (w.Touched, hits.Value) |> should equal (1, 11)

[<Fact>]
let ``an int argument fits an int64 function domain by C#'s implicit widening`` () =
    let h = box (Holders())
    (dlr { return h?Wide(41) } : int64) |> should equal 42L
    let e = box (Fixtures.expando [ "F", box (fun (x: float) -> x * 2.0) ])
    (dlr { return e?F(21) } : float) |> should equal 42.0
    // A boxed int (dynamic argument) widens the same way: unboxed as int, then converted.
    let n = box 41
    (dlr { return h?Wide(n) } : int64) |> should equal 42L
    (dlr { return e?F(n) } : float) |> should equal 82.0
    let m = box 2
    (dlr { return h?WideTupled(n, m) } : int64) |> should equal 43L
    (dlr { return h?WideCurried(n, m) } : int64) |> should equal 43L

[<Fact>]
let ``optional-parameter overloads pick the more specific one deterministically`` () =
    let h = box (Holders())
    let s = "x"
    (dlr { return h?Overloaded(s) } : string) |> should equal "string:x"
    (dlr { return h?Overloaded(box 1) } : string) |> should equal "obj:1"

[<Fact>]
let ``a protected member binds from a derived context`` () =
    let d = Derived()
    d.CallFamily(d) |> should equal 15

[<Fact>]
let ``calling an F# function member has no arity limit`` () =
    let h = box (Holders())
    (dlr { return h?Five(1, 2, 3, 4, 5) } : int) |> should equal 15
    (dlr { return h?Six(1, 2, 3, 4, 5, 6) } : int) |> should equal 720
    (dlr { return h?SixTupled(1, 2, 3, 4, 5, 6) } : int) |> should equal 21
    (dlr { return h?Eight(1, 2, 3, 4, 5, 6, 7, 8) } : int) |> should equal 36
    let six = box (fun (a: int) (b: int) (c: int) (d: int) (e: int) (f: int) -> a + b + c + d + e + f)
    (dlr { return six |> Dlr.call (1, 1, 1, 1, 1, 1) } : int) |> should equal 6

[<Fact>]
let ``reading a member as a curried function has no arity limit`` () =
    let h = box (Holders())
    let five: int -> int -> int -> int -> int -> int = dlr { return h?Five }
    five 1 2 3 4 5 |> should equal 15
    // Past five, a run-time-built curried closure, as F# itself does past OptimizedClosures.
    let six: int -> int -> int -> int -> int -> int -> int = dlr { return h?Six }
    six 1 2 3 4 5 6 |> should equal 720
    let partial = six 1 2 3
    partial 4 5 6 |> should equal 720
    let eight: int -> int -> int -> int -> int -> int -> int -> int -> int64 = dlr { return h?Eight }
    eight 1 2 3 4 5 6 7 8 |> should equal 36L
    // A C# method of six parameters, bound curried.
    let sum6: int -> int -> int -> int -> int -> int -> int = dlr { return (box (Widget()))?Sum6 }
    sum6 1 2 3 4 5 6 |> should equal 21
    // A unit result past five: the site is void, the last step returns unit.
    let sixUnit: int -> int -> int -> int -> int -> int -> unit = dlr { return h?Six }
    sixUnit 1 2 3 4 5 6
    // Tupled reads keep the five-element limit of the typed helpers.
    (fun () -> (dlr { return h?SixTupled } : int * int * int * int * int * int -> int) |> ignore)
    |> should throw typeof<DlrTranslationException>

[<Fact>]
let ``a method of only optional parameters can be read as unit -> R`` () =
    let o = box (Widget())
    let wrap: unit -> string = dlr { return o?Wrap }
    wrap () |> should equal "<x>"
