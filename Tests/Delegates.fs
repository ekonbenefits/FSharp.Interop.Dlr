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
    let ran = Func<string>(fun () -> "ran")      // built outside: a zero-argument delegate literal has no quotation form the converter takes
    (dlr { return o?Run(ran) } : string) |> should equal "ran"
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
