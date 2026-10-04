[<ReflectedDefinition>]
module Tests.Delegates

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``an F# lambda is converted to a delegate parameter`` () =
    let c = Callbacks()
    let o = box c
    let seen = ResizeArray<int>()
    dlr { o?Each([ 1; 2; 3 ], fun (i: int) -> seen.Add i) }
    List.ofSeq seen |> should equal [ 1; 2; 3 ]
    (dlr { return o?Map(20, fun (x: int) -> x + 1) } : int) |> should equal 21
    (dlr { return o?Fold(3, 4, fun (a: int) (b: int) -> a * b) } : int) |> should equal 12     // curried into Func<int,int,int>
    (dlr { return o?Fold(3, 4, fun (a: int, b: int) -> a - b) } : int) |> should equal -1      // tupled too
    let f = fun (x: int) -> x * 2
    (dlr { return o?Map(21, f) } : int) |> should equal 42                                     // a function value, typed
    (dlr { return o?Map(21, box f) } : int) |> should equal 42                                 // and as obj, by runtime type
    // Past five parameters a compiled lambda takes over: any arity the delegate allows.
    (dlr { return o?Six(fun a b c d e (f: int) -> a + b + c + d + e + f) } : int) |> should equal 21
    (dlr { return o?Six(fun (a, b, c, d, e, f: int) -> a * b * c * d * e * f) } : int) |> should equal 720

[<Fact>]
let ``a delegate is converted to an F# function parameter`` () =
    let o = box (Callbacks())
    (dlr { return o?Apply(20, Func<int, int>(fun x -> x + 1)) } : int) |> should equal 21
    (dlr { return o?Apply2(3, 4, Func<int, int, int>(fun a b -> a * b)) } : int) |> should equal 12
    (dlr { return o?ApplyTupled(3, 4, Func<int, int, int>(fun a b -> a - b)) } : int) |> should equal -1
    (dlr { return o?Run(Func<string>(fun () -> "ran")) } : string) |> should equal "ran"   // parameterless (#156)
    (dlr { return o?Six'(Func<int, int, int, int, int, int, int>(fun a b c d e f -> a + b + c + d + e + f)) } : int) |> should equal 21
    // Past five, a function compiled once per delegate type: curried (each partial application its
    // own), tupled (the tuple nested past seven), and unit for an Action.
    (dlr { return o?SixPartial(Func<int, int, int, int, int, int, int>(fun a b c d e f -> a + b + c + d + e + f)) } : int) |> should equal 21006
    (dlr { return o?SixTupled'(Func<int, int, int, int, int, int, int>(fun a b c d e f -> a * b * c * d * e * f)) } : int) |> should equal 720
    (dlr { return o?EightTupled'(Func<int, int, int, int, int, int, int, int, int>(fun a b c d e f g h -> a + b + c + d + e + f + g + h)) } : int) |> should equal 36
    let seen = ResizeArray<int>()
    let record = Action<int, int, int, int, int, int>(fun a b c d e f -> seen.AddRange [ a; b; c; d; e; f ])
    (dlr { return o?SixUnit'(record) } : unit)
    List.ofSeq seen |> should equal [ 1; 2; 3; 4; 5; 6 ]

[<Fact>]
let ``a delegate returning F#'s unit converts to an F# function returning unit`` () =
    // A Func<…, unit> (not an Action): its CLR return type is FSharp.Core.Unit itself.
    let o = box (Callbacks())
    let seen = ResizeArray<int>()
    (dlr { return o?Do(41, Func<int, unit>(fun x -> seen.Add(x + 1))) } : unit)
    (dlr { return o?Do2(Func<int, int, unit>(fun a b -> seen.Add(a + b))) } : unit)
    (dlr { return o?SixUnit'(Func<int, int, int, int, int, int, unit>(fun a b c d e f -> seen.Add(a + b + c + d + e + f))) } : unit)
    List.ofSeq seen |> should equal [ 42; 3; 21 ]

[<Fact>]
let ``a delegate for a function over a one-element tuple is C#'s error`` () =
    // No typed wrapper takes a 1-tuple domain; the binder does not offer the conversion (it once
    // bound through a DynamicInvoke fallback and failed mid-call).
    let o = box (Callbacks())
    (fun () -> (dlr { return o?OneTuple(Func<int, int>(fun x -> x + 1)) } : int) |> ignore)
    |> should throw typeof<Microsoft.CSharp.RuntimeBinder.RuntimeBinderException>

[<Fact>]
let ``a tupled F# function reaches a delegate parameter past the adapter classes`` () =
    let o = box (Callbacks())
    // Seventeen parameters: no adapter class, so the conversion applies the function itself and
    // builds its (nested) tuple.
    let sum: int = dlr { return o?Wide(fun (a: int, b: int, c: int, d: int, e: int, f: int, g: int, h: int, i: int, j: int, k: int, l: int, m: int, n: int, o: int, p: int, q: int) -> a + b + c + d + e + f + g + h + i + j + k + l + m + n + o + p + q) }
    sum |> should equal 153

[<Fact>]
let ``a delegate literal in a block reports its own signature through .Method`` () =
    let o = box (Callbacks())
    let mutable captured = 0
    // Compiled with the block it would be a DynamicMethod delegate with a hidden `Closure` first
    // parameter; consumers that marshal by `.Method` (NLua, event-wiring helpers) refuse that.
    let kept: Func<int, int> = dlr { return o?Keep(Func<int, int>(fun x -> captured <- x; x + 1)) }
    [ for p in kept.Method.GetParameters() -> p.ParameterType ] |> should equal [ typeof<int> ]
    kept.Method.ReturnType |> should equal typeof<int>
    kept.Invoke 41 |> should equal 42
    captured |> should equal 41
    // An F# `internal` delegate type: its `Invoke` and constructor are internal too, unlike C#'s.
    let internal': InternalHandler = dlr { return o?KeepInternal(InternalHandler(fun x -> x + 1)) }
    [ for p in internal'.Method.GetParameters() -> p.ParameterType ] |> should equal [ typeof<int> ]
    internal'.Invoke 41 |> should equal 42
    // An F# function for that internal delegate parameter: the conversion reads its internal Invoke.
    let fromFunction: InternalHandler = dlr { return o?KeepInternal(fun (x: int) -> x * 2) }
    fromFunction.Invoke 21 |> should equal 42

type private PrivateHandler = delegate of int -> int
type private PrivateHolder() =
    member _.Keep(f: PrivateHandler) = f
    member _.Apply(x: int, f: int -> int) = f x

[<Fact>]
let ``an internal F# delegate converts to an F# function parameter`` () =
    // Its `Invoke` is internal (#150): a typed wrapper up to five, a compiled factory past it.
    let o = box (Callbacks())
    (dlr { return o?Apply2(3, 4, InternalAdd(fun a b -> a * 10 + b)) } : int) |> should equal 34
    (dlr { return o?ApplyTupled(3, 4, InternalAdd(fun a b -> a - b)) } : int) |> should equal -1
    (dlr { return o?Six'(InternalSix(fun a b c d e f -> a + b + c + d + e + f)) } : int) |> should equal 21
    (dlr { return o?SixTupled'(InternalSix(fun a b c d e f -> a * b * c * d * e * f)) } : int) |> should equal 720
    let seen = ResizeArray<int>()
    let record = InternalSixAction(fun a b c d e f -> seen.AddRange [ a; b; c; d; e; f ])   // built outside the block too
    (dlr { return o?SixUnit'(record) } : unit)
    List.ofSeq seen |> should equal [ 1; 2; 3; 4; 5; 6 ]
    // And an internal delegate value invoked or read as a function, `Dlr.call`.
    let add = box (InternalAdd(fun a b -> a * 10 + b))
    let six = box (InternalSix(fun a b c d e f -> a + b + c + d + e + f))
    (dlr { return Dlr.call add (3, 4) } : int) |> should equal 34
    (dlr { return Dlr.call six (1, 2, 3, 4, 5, 6) } : int) |> should equal 21
    (dlr { return Dlr.call add } : int -> int -> int) 3 4 |> should equal 34
    (dlr { return Dlr.call six } : int -> int -> int -> int -> int -> int -> int) 1 2 3 4 5 6 |> should equal 21
    // Through our rule (C# cannot pass a Func for its F# function parameter): the internal Invoke too.
    let apply = box (InternalApply(fun f x -> f x))
    (dlr { return Dlr.call apply (Func<int, int>(fun x -> x + 1), 41) } : int) |> should equal 42

[<Fact>]
let ``an internal delegate value is invoked: a member of its type, an Expando's, Dlr.call`` () =
    // On .NET Framework C#'s Invoke binder cannot (Expression.Invoke's public-only lookup): ours goes first there.
    let o = box (Callbacks())
    (dlr { return o?Adder(2, 3) } : int) |> should equal 203
    let e = box (Fixtures.expando [ "Add", box (InternalAdd(fun a b -> a + b)) ])
    (dlr { return e?Add(20, 22) } : int) |> should equal 42
    (dlr { return Dlr.call (box (InternalAdd(fun a b -> a - b))) (5, 3) } : int) |> should equal 2

[<Fact>]
let ``internal delegates past sixteen parameters, events of one, and a private delegate`` () =
    let o = box (Callbacks())
    // Past sixteen: a function to the delegate is a compiled lambda, the delegate to a function a factory.
    (dlr { return o?WideInternal(fun (a: int, b: int, c: int, d: int, e: int, f: int, g: int, h: int, i: int, j: int, k: int, l: int, m: int, n: int, p: int, q: int, r: int) ->
                                     a + b + c + d + e + f + g + h + i + j + k + l + m + n + p + q + r) } : int) |> should equal 153
    let wide = InternalWide17(fun a b c d e f g h i j k l m n p q r -> a + b + c + d + e + f + g + h + i + j + k + l + m + n + p + q + r)
    (dlr { return o?Wide17Tupled(wide) } : int) |> should equal 153
    (dlr { return o?Wide17Curried(wide) } : int) |> should equal 153
    (dlr { return Dlr.call (box wide) (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17) } : int) |> should equal 153
    // An event of an internal delegate type: a delegate, then an F# function converted to it.
    let clicker = InternalClicker()
    let c = box clicker
    let seen = ResizeArray<int>()
    let handler = InternalNotify(fun _ n -> seen.Add n)
    dlr { c |> Dlr.addAssign "Changed" handler }
    clicker.Raise 1
    dlr { c |> Dlr.subtractAssign "Changed" handler }
    clicker.Raise 2
    dlr { c |> Dlr.addAssign "Changed" (fun (_: obj) (n: int) -> seen.Add(n * 10)) }
    clicker.Raise 3
    List.ofSeq seen |> should equal [ 1; 30 ]
    // A private delegate type: a literal in the block, from a function, to a function, invoked.
    let h = box (PrivateHolder())
    let kept: PrivateHandler = dlr { return h?Keep(PrivateHandler(fun x -> x + 1)) }
    kept.Invoke 41 |> should equal 42
    let fromFunction: PrivateHandler = dlr { return h?Keep(fun (x: int) -> x * 2) }
    fromFunction.Invoke 21 |> should equal 42
    (dlr { return h?Apply(20, PrivateHandler(fun x -> x + 1)) } : int) |> should equal 21
    (dlr { return Dlr.call (box kept) 41 } : int) |> should equal 42

[<Fact>]
let ``overloads: the delegate parameter is one candidate among others`` () =
    let o = box (Callbacks())
    (dlr { return o?Pick(1, fun (x: int) -> x + 1) } : string) |> should equal "func:2"
    (dlr { return o?Pick(1, "s") } : string) |> should equal "string:s"
    // A function whose shape does not fit any candidate is still a binder error.
    (fun () -> (dlr { return o?Map(1, fun (s: string) -> s.Length) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>
    // A parameter typed System.Delegate (Control.Invoke): the Func/Action F# would build, not the
    // Converter<Unit, R> C# reaches through FSharpFunc's op_Implicit (a one-parameter delegate,
    // which a DynamicInvoke() then rejects).
    (dlr { return o?Raw(fun () -> "x") } : string) |> should equal "Func`1"
    (dlr { return o?Marshal(fun () -> "marshalled") } : string) |> should equal "marshalled"
    (dlr { return o?Raw(fun (i: int) -> i) } : string) |> should equal "Func`2"
    (dlr { return o?Raw(fun () -> ()) } : string) |> should equal "Action"
    let raw = Func<string>(fun () -> "raw")
    (dlr { return o?Raw(raw) } : string) |> should equal "Func`1"
    (dlr { return o?Raw(fun a b c d e (f: int) -> a + b + c + d + e + f) } : string) |> should equal "Func`7"
    // A delegate still binds directly, as before.
    (dlr { return o?Map(20, Func<int, int>(fun x -> x + 2)) } : int) |> should equal 22

[<Fact>]
let ``one site alternates delegate and function arguments`` () =
    let o = box (Callbacks())
    let args: obj list = [ box (Func<int, int>(fun x -> x + 1)); box (fun (x: int) -> x + 10); box (Func<int, int>(fun x -> x + 100)) ]
    let results = ResizeArray<int>()
    dlr {
        for a in args do
            results.Add(o?Map(1, a))
    }
    List.ofSeq results |> should equal [ 2; 11; 101 ]

[<Fact>]
let ``a delegate's exception through an F# function parameter past five arrives as itself`` () =
    let t = box (Throwers())
    let throwing = Func<int, int, int, int, int, int, int>(fun _ _ _ _ _ _ -> raise (InvalidOperationException "boom"))
    (fun () -> (dlr { return t?ApplyCurried6(throwing) } : int) |> ignore) |> should throw typeof<InvalidOperationException>
    (fun () -> (dlr { return t?ApplyTupled6(throwing) } : int) |> ignore) |> should throw typeof<InvalidOperationException>

[<Fact>]
let ``an F# function assigned to a delegate-typed property, field, indexer or array element`` () =
    // C# cannot assign an FSharpFunc to a delegate slot; ours converts by the slot's type (#153).
    let s = Slots()
    let o = box s
    dlr { o?Handler <- (fun (a: int) (b: int) -> a - b) }
    s.Handler.Invoke(5, 3) |> should equal 2
    dlr { o?Handler <- (fun (a: int, b: int) -> a * b) }                 // tupled too
    s.Handler.Invoke(5, 3) |> should equal 15
    let said = ResizeArray<string>()
    dlr { o?Notify <- (fun (m: string) -> said.Add m) }                  // unit result: an Action
    s.Notify.Invoke "hi"
    List.ofSeq said |> should equal [ "hi" ]
    dlr { o?Adder <- (fun (a: int) (b: int) -> a * 10 + b) }             // an internal delegate type
    s.Adder.Invoke(3, 4) |> should equal 34
    dlr { o?Wide <- (fun a b c d e (f: int) -> a + b + c + d + e + f) }  // past five
    s.Wide.Invoke(1, 2, 3, 4, 5, 6) |> should equal 21
    dlr { o?Field <- (fun (x: int) -> x + 1) }                           // a field
    s.Field.Invoke 41 |> should equal 42
    let handlers = box s.Handlers
    dlr { handlers |> Dlr.setItem "double" (fun (x: int) -> x * 2) }             // an indexer
    s.Handlers.["double"].Invoke 21 |> should equal 42
    let array = box s.Array
    dlr { array |> Dlr.setItem 1 (fun (x: int) -> x - 1) }                       // an array element
    s.Array.[1].Invoke 43 |> should equal 42

[<Fact>]
let ``a delegate assigned to an F# function-typed property or indexer`` () =
    let s = Slots()
    let o = box s
    dlr { o?Fn <- Func<int, int, int>(fun a b -> a * b) }
    s.Fn 6 7 |> should equal 42
    let functions = box s.Functions
    dlr { functions |> Dlr.setItem "inc" (Func<int, int>(fun x -> x + 1)) }
    s.Functions.["inc"] 41 |> should equal 42

[<Fact>]
let ``assignments C# binds itself stay C#'s, and a slot no conversion fits is C#'s error`` () =
    let s = Slots()
    let o = box s
    dlr { o?Handler <- Func<int, int, int>(fun a b -> a - b) }           // a delegate of the slot's type
    s.Handler.Invoke(5, 3) |> should equal 2
    dlr { o?Conv <- (fun (x: int) -> x + 1) }                            // FSharpFunc's op_Implicit: C# binds it
    s.Conv.Invoke 41 |> should equal 42
    // A function of the wrong shape for the slot: no conversion, C#'s error.
    (fun () -> dlr { o?Handler <- (fun (x: string) -> x.Length) }) |> should throw typeof<Microsoft.CSharp.RuntimeBinder.RuntimeBinderException>

[<Fact>]
let ``one assignment site over alternating values and targets`` () =
    // Polymorphic: a function, a delegate, a function again into the same slot; then an Expando.
    let s = Slots()
    let set (target: obj) (value: obj) = dlr { target?Handler <- value }
    for i in 1 .. 3 do
        set (box s) (box (fun (a: int) (b: int) -> a + b + i))
        s.Handler.Invoke(1, 1) |> should equal (2 + i)
        set (box s) (box (Func<int, int, int>(fun a b -> a * b * i)))
        s.Handler.Invoke(2, 3) |> should equal (6 * i)
    let e = Fixtures.expando []
    set (box e) (box (fun (a: int) (b: int) -> a - b))
    (dlr { return (box e)?Handler(5, 3) } : int) |> should equal 2

[<Fact>]
let ``an assignment converted into a boxed struct lands in the box, as C#'s does`` () =
    let o = box (Tests.CSharp.MutableSlot())
    dlr { o?Number <- 5 }                                                 // C#'s own: the box mutates
    dlr { o?Property <- (fun (x: int) -> x + 1) }                         // ours, through the setter
    dlr { o?Field <- (fun (x: int) -> x + 2) }                            // ours, the field
    let slot = unbox<Tests.CSharp.MutableSlot> o
    slot.Number |> should equal 5
    slot.Property.Invoke 41 |> should equal 42
    slot.Field.Invoke 40 |> should equal 42
    // A mutating method called through our fallback (an F# function for its Func): the box too.
    dlr { o?Store(Func<int, int>(fun x -> x + 3)) }                       // C#'s own call
    (unbox<Tests.CSharp.MutableSlot> o).Property.Invoke 39 |> should equal 42
    dlr { o?Store(fun (x: int) -> x + 4) }                                // ours
    (unbox<Tests.CSharp.MutableSlot> o).Property.Invoke 38 |> should equal 42

[<Fact>]
let ``an F# function assigned to a Delegate-typed slot is the Func or Action of its signature`` () =
    // C# would bind FSharpFunc's op_Implicit Converter<Unit, R>, which DynamicInvoke() rejects: ours goes first.
    let s = Tests.CSharp.AbstractSlots()
    let o = box s
    dlr { o?Property <- (fun () -> 42) }
    s.Property.GetType() |> should equal typeof<Func<int>>
    s.Property.DynamicInvoke() |> should equal (box 42)
    dlr { o?Field <- (fun (a: int) (b: int) -> a + b) }
    s.Field.DynamicInvoke(20, 22) |> should equal (box 42)
    let map = box s.Map
    dlr { map |> Dlr.setItem "run" (fun () -> 42) }
    s.Map.["run"].DynamicInvoke() |> should equal (box 42)
    // A delegate is C#'s to assign, as it is.
    let given = Action(ignore)
    dlr { o?Property <- given }
    obj.ReferenceEquals(s.Property, given) |> should equal true

[<Fact>]
let ``an array element converted at any index type C# takes`` () =
    let array = box (Array.zeroCreate<Func<int, int>> 3)
    dlr { array |> Dlr.setItem 1L (fun (x: int) -> x + 1) }
    dlr { array |> Dlr.setItem 2u (fun (x: int) -> x + 2) }
    let small: byte = 0uy
    dlr { array |> Dlr.setItem small (fun (x: int) -> x + 3) }                  // typed, widening to int
    let boxed: obj = box 1s
    dlr { array |> Dlr.setItem boxed (fun (x: int) -> x + 4) }                  // boxed: unboxed at its runtime type
    let a = unbox<Func<int, int>[]> array
    a.[0].Invoke 39 |> should equal 42
    a.[1].Invoke 38 |> should equal 42
    a.[2].Invoke 40 |> should equal 42

[<Fact>]
let ``a parameterless delegate literal in a block`` () =
    // F# quotes `Func<int>(fun () -> 7)` with no parameter and a bare body; FSharp.Core takes that
    // node apart as `fun () -> 7` and would not put it back together (#156).
    let made: Func<int> = dlr { return Func<int>(fun () -> 7) }
    made.Invoke() |> should equal 7
    made.Method.GetParameters().Length |> should equal 0                    // its own signature through .Method
    let mutable ran = 0
    let action: Action = dlr { return Action(fun () -> ran <- ran + 1) }    // capturing a mutable
    action.Invoke()
    ran |> should equal 1
    let s = Slots()
    let o = box s
    dlr { o?Thunk <- Func<int>(fun () -> 42) }                              // assigned
    s.Thunk.Invoke() |> should equal 42
    (dlr { return o?Call(Func<int>(fun () -> 21)) } : int) |> should equal 42   // passed to a Func<int> parameter
    let thunk: InternalThunk = dlr { return InternalThunk(fun () -> "internal") }   // an internal delegate type
    thunk.Invoke() |> should equal "internal"
    // By the literal's own type, not by guessing from the body: a result that is itself an F#
    // function, a `unit` result of a non-void delegate, a framework delegate type.
    let adder: Func<int -> int> = dlr { return Func<int -> int>(fun () -> fun x -> x + 22) }
    adder.Invoke() 20 |> should equal 42
    let calls = ResizeArray<int>()
    let unitFunc: Func<unit> = dlr { return Func<unit>(fun () -> calls.Add 1) }
    unitFunc.Invoke()
    List.ofSeq calls |> should equal [ 1 ]
    let start: Threading.ThreadStart = dlr { return Threading.ThreadStart(fun () -> calls.Add 2) }
    start.Invoke()
    List.ofSeq calls |> should equal [ 1; 2 ]
    // What the body throws arrives as itself.
    let throws: Func<int> = dlr { return Func<int>(fun () -> invalidOp "boom") }
    (fun () -> throws.Invoke() |> ignore) |> should throw typeof<InvalidOperationException>
