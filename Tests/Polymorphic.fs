/// One site, several kinds of target, alternating repeatedly: the DLR caches one rule per kind
/// and must pick the right one every time, not just on the first bind.
[<ReflectedDefinition>]
module Tests.Polymorphic

open System
open System.Collections.Generic
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

type Counted(n: int) =
    member _.Count = n
    member _.Add(a: int, b: int) = a + b + n

let private rounds = 5

[<Fact>]
let ``get member: CLR object, Expando, DynamicObject and a dictionary through one site`` () =
    let read (o: obj) : int = dlr { return o?Count }
    let clr = box (Counted(1))
    let expando = box (Fixtures.expando [ "Count", box 2 ])
    let dyn = box (Counter(3))
    let dict = box (Dictionary<string, int>(dict [ "x", 0; "y", 0; "z", 0; "w", 0 ]))
    for _ in 1 .. rounds do
        [ read clr; read expando; read dyn; read dict ] |> should equal [ 1; 2; 3; 4 ]

[<Fact>]
let ``invoke member: a method, a delegate member and an F# function member through one site`` () =
    let call (o: obj) : int = dlr { return o?Add(10, 5) }
    let method' = box (Counted(100))
    let delegate' = box (Fixtures.expando [ "Add", box (Func<int, int, int>(fun a b -> a * b)) ])
    let curried = box (Fixtures.expando [ "Add", box (fun (a: int) (b: int) -> a - b) ])
    let tupled = box (Fixtures.expando [ "Add", box (fun (a: int, b: int) -> a + b) ])
    let record = box {| Add = fun (a: int) (b: int) -> a * 100 + b |}
    for _ in 1 .. rounds do
        [ call method'; call delegate'; call curried; call tupled; call record ] |> should equal [ 115; 50; 5; 15; 1005 ]

[<Fact>]
let ``read as a function type: alternating kinds behind one bound function`` () =
    // The bound function holds one site; each application sees whichever target it was bound to.
    let bind (o: obj) : int -> int -> int = dlr { return o?Add }
    let fromMethod = bind (Counted(100))
    let fromDelegate = bind (Fixtures.expando [ "Add", box (Func<int, int, int>(fun a b -> a * b)) ])
    let fromFunction = bind (Fixtures.expando [ "Add", box (fun (a: int) (b: int) -> a - b) ])
    for _ in 1 .. rounds do
        [ fromMethod 10 5; fromDelegate 10 5; fromFunction 10 5 ] |> should equal [ 115; 50; 5 ]

[<Fact>]
let ``read as a delegate type: a method, a held delegate, a dynamic object's own and a proxy through one site`` () =
    // #201: the marker rule (a method, a dynamic object with nothing to read) and plain reads of a
    // held delegate alternate at one site; a held delegate comes back as itself.
    let bind (o: obj) : Func<int, int, int> = dlr { return o?Add }
    let held = Func<int, int, int>(fun a b -> a * b)
    let method' = box (Counted(100))
    let property = box {| Add = held |}
    let expando = box (Fixtures.expando [ "Add", box held ])
    let dynamicOwn = box (DynamicAdd())                     // its own Add over its CLR type's method
    let proxy = box (InvokeOnly())                          // nothing to read: asked at the call
    for _ in 1 .. rounds do
        [ bind method'; bind property; bind expando; bind dynamicOwn; bind proxy ]
        |> List.map (fun f -> f.Invoke(10, 5))
        |> should equal [ 115; 50; 50; 1015; 15 ]
        obj.ReferenceEquals(bind property, held) |> should equal true
        obj.ReferenceEquals(bind expando, held) |> should equal true

[<Fact>]
let ``converted to a delegate type: a delegate, two kinds of F# function, a TryConvert object and null through one site`` () =
    // #202: C#'s rules (the delegate itself, TryConvert, null) and ours (an F# function) alternate.
    let toFunc (v: obj) : Func<int, int> = dlr { return Dlr.implicit v }
    let held = Func<int, int>(fun x -> x * 2)
    let delegate' = box held
    let lambda = box (fun (x: int) -> x + 100)
    let partial = box ((fun (a: int) (b: int) -> a * b) 7)        // another closure type
    let convertible = box (ConvertsToFunc())
    let none: obj = null
    for _ in 1 .. rounds do
        [ toFunc delegate'; toFunc lambda; toFunc partial; toFunc convertible ]
        |> List.map (fun f -> f.Invoke 3)
        |> should equal [ 6; 103; 21; 2 ]
        obj.ReferenceEquals(toFunc delegate', held) |> should equal true
        isNull (toFunc none) |> should equal true

[<Fact>]
let ``unit -> R: a property, a parameterless method, a delegate and an F# function through one site`` () =
    let bind (o: obj) : unit -> int = dlr { return o?Value }
    let property = box {| Value = 1 |}
    let method' = box (Fixtures.expando [ "Value", box (Func<int>(fun () -> 2)) ])
    let fn = box (Fixtures.expando [ "Value", box (fun () -> 3) ])
    let plain = box (Fixtures.expando [ "Value", box 4 ])
    let p, m, f, v = bind property, bind method', bind fn, bind plain
    for _ in 1 .. rounds do
        [ p (); m (); f (); v () ] |> should equal [ 1; 2; 3; 4 ]

[<Fact>]
let ``Dlr.apply: a delegate and an F# function through one site`` () =
    let call (f: obj) : int = dlr { return f |> Dlr.apply 21 }
    let del = box (Func<int, int>(fun x -> x * 2))
    let fn = box (fun (x: int) -> x + 1)
    for _ in 1 .. rounds do
        [ call del; call fn ] |> should equal [ 42; 22 ]

[<Fact>]
let ``set member and index through one site over different kinds`` () =
    let set (o: obj) (v: int) = dlr { o?Count <- v }
    let w = Widget()
    let e = Fixtures.expando []
    for i in 1 .. rounds do
        set (box w) i
        set (box e) (i * 10)
        w.Count |> should equal i
        (dlr { return (box e)?Count } : int) |> should equal (i * 10)
    let index (o: obj) (k: obj) : int = dlr { return o |> Dlr.item k }
    let dict = Dictionary<string, int>(dict [ "a", 1 ])
    let arr = [| 7; 8 |]
    for _ in 1 .. rounds do
        [ index dict "a"; index arr 1 ] |> should equal [ 1; 8 ]

[<Fact>]
let ``operators through one site over int, float, string and a DynamicObject`` () =
    let add (a: obj) (b: obj) : obj = dlr { return a ?+? b }
    for _ in 1 .. rounds do
        add 1 2 |> should equal (box 3)
        add 1.5 2.0 |> should equal (box 3.5)
        add "a" "b" |> should equal (box "ab")
        (add (Arith(2)) (Arith(3)) :?> Arith).Value |> should equal 5
