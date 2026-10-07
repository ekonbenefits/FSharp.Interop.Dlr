/// `Dlr.cast<T>` (C#'s explicit cast) and `Dlr.implicit` (the implicit conversion a `?` result gets on its own).
[<ReflectedDefinition>]
module Tests.Conversions

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``explicit cast truncates where the implicit conversion refuses`` () =
    let w: obj = Widget()
    let truncated: int = dlr { return Dlr.cast<int> w?Ratio }
    truncated |> should equal 2
    (fun () -> (dlr { return w?Ratio } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``explicit cast on a typed value and on an enum`` () =
    let d = 3.9
    let truncated = dlr { return Dlr.cast<int> d }
    let day = dlr { return Dlr.cast<System.DayOfWeek> 2 }
    truncated |> should equal 3
    day |> should equal System.DayOfWeek.Tuesday

[<Fact>]
let ``implicit conversion of a held value`` () =
    let x = box 5
    let widened: int64 = dlr { return Dlr.implicit x }
    widened |> should equal 5L
    let same: int = dlr { return Dlr.implicit x }
    same |> should equal 5
    let asObj: obj = dlr { return Dlr.implicit x }
    asObj |> should equal (box 5)

[<Fact>]
let ``implicit conversion uses op_Implicit and TryConvert`` () =
    let n = box 7
    let m: Meters = dlr { return Dlr.implicit n }
    m.Value |> should equal 7
    let a: obj = Arith(9)
    let text: string = dlr { return Dlr.implicit a }
    text |> should equal "9"

[<Fact>]
let ``implicit conversion refuses what a cast would allow`` () =
    let d = box 3.9
    (fun () -> (dlr { return Dlr.implicit d } : int) |> ignore) |> should throw typeof<RuntimeBinderException>
    let truncated: int = dlr { return Dlr.cast<int> d }
    truncated |> should equal 3

// #202: a function value converted to a delegate type, and a delegate to a function type, as an
// argument or an assignment already is; C# sees an FSharpFunc and a Func as unrelated.

[<Fact>]
let ``an F# function converts to a delegate type, and a delegate to an F# function type`` () =
    let h: obj = Holders()
    let fromProperty: Func<int, int> = dlr { return h?AsFunction }           // int -> int, x + 1
    let fromObj: Func<int, int> = dlr { return h?AsObj }                     // an obj holding one, x * 3
    fromProperty.Invoke 1 |> should equal 2
    fromObj.Invoke 1 |> should equal 3
    let f = box (fun (x: int) -> x + 7)
    let implicit': Func<int, int> = dlr { return Dlr.implicit f }
    let cast: Func<int, int> = dlr { return Dlr.cast<Func<int, int>> f }
    implicit'.Invoke 1 |> should equal 8
    cast.Invoke 1 |> should equal 8
    let m: obj = Makers()
    let madeFunction: Func<int, int> = dlr { return m?MakeFunction() }      // a call's result
    madeFunction.Invoke 1 |> should equal 11
    let d = box (Func<int, int>(fun x -> x * 9))
    let back: int -> int = dlr { return Dlr.implicit d }
    let backCast: int -> int = dlr { return Dlr.cast<int -> int> d }
    let madeDelegate: int -> int = dlr { return m?MakeDelegate() }
    back 1 |> should equal 9
    backCast 1 |> should equal 9
    madeDelegate 1 |> should equal 21

[<Fact>]
let ``function to delegate and back at each arity: unit, one, two curried and tupled, past five`` () =
    let thunk = box (fun () -> 5)
    let toFunc0: Func<int> = dlr { return Dlr.implicit thunk }
    toFunc0.Invoke() |> should equal 5
    let log = ResizeArray<int>()
    let sink = box (fun (x: int) -> log.Add x)
    let toAction: Action<int> = dlr { return Dlr.implicit sink }
    toAction.Invoke 4
    log |> List.ofSeq |> should equal [ 4 ]
    let curried = box (fun (a: int) (b: int) -> a - b)
    let tupled = box (fun (a: int, b: int) -> a * b)
    let fromCurried: Func<int, int, int> = dlr { return Dlr.implicit curried }
    let fromTupled: Func<int, int, int> = dlr { return Dlr.implicit tupled }
    fromCurried.Invoke(5, 2) |> should equal 3
    fromTupled.Invoke(5, 2) |> should equal 10
    let six = box (fun (a: int) (b: int) (c: int) (d: int) (e: int) (f: int) -> a + b + c + d + e + f)
    let toFunc6: Func<int, int, int, int, int, int, int> = dlr { return Dlr.implicit six }
    toFunc6.Invoke(1, 2, 3, 4, 5, 6) |> should equal 21
    let func6 = box (Func<int, int, int, int, int, int, int>(fun a b c d e f -> a * b * c * d * e * f))
    let toCurried6: int -> int -> int -> int -> int -> int -> int = dlr { return Dlr.implicit func6 }
    toCurried6 1 2 3 4 5 6 |> should equal 720
    let action = box (Action<int>(fun x -> log.Add (x * 10)))
    let toUnitFunction: int -> unit = dlr { return Dlr.implicit action }
    toUnitFunction 3
    log |> List.ofSeq |> should equal [ 4; 30 ]

[<Fact>]
let ``to Delegate itself, the Func or Action of the function's own signature, not C#'s Converter`` () =
    // Left to C#, FSharpFunc's op_Implicit makes a Converter<Unit, int> of a `unit -> int`, which a
    // DynamicInvoke() with no arguments cannot call: ours goes first for `Delegate` itself.
    let thunk = box (fun () -> 5)
    let asDelegate: Delegate = dlr { return Dlr.implicit thunk }
    asDelegate.GetType() |> should equal typeof<Func<int>>
    asDelegate.DynamicInvoke() |> should equal (box 5)
    let h: obj = Holders()
    let property: Delegate = dlr { return h?AsFunction }
    property.GetType() |> should equal typeof<Func<int, int>>
    property.DynamicInvoke(box 1) |> should equal (box 2)

[<Fact>]
let ``C#'s own conversions are kept: a delegate is itself, a function to a Converter through op_Implicit`` () =
    let held = Func<int, int>(fun x -> x + 1)
    let d = box held
    let same: Func<int, int> = dlr { return Dlr.implicit d }
    obj.ReferenceEquals(same, held) |> should equal true
    let f = box (fun (x: int) -> x + 7)
    let converter: Converter<int, int> = dlr { return Dlr.implicit f }
    converter.Invoke 1 |> should equal 8
    let n: obj = null
    let none: Func<int, int> = dlr { return Dlr.implicit n }
    isNull none |> should equal true

[<Fact>]
let ``an internal delegate type, both ways`` () =
    let curried = box (fun (a: int) (b: int) -> a + b)
    let pair: InternalPair = dlr { return Dlr.implicit curried }
    pair.Invoke(3, 4) |> should equal 7
    let p = box (InternalPair(fun a b -> a * b))
    let back: int -> int -> int = dlr { return Dlr.implicit p }
    back 3 4 |> should equal 12

[<Fact>]
let ``a function that does not fit the delegate fails as C# does`` () =
    let f = box (fun (x: int) -> string x)
    let ex = AnyUnit.Run.Assert.Current.Throws<RuntimeBinderException>(fun () -> (dlr { return Dlr.implicit f } : Func<int, int>) |> ignore)
    // Browser-wasm trims resource strings, leaving the key.
    if string Runtime.InteropServices.RuntimeInformation.OSArchitecture <> "Wasm" then
        ex.Message |> should haveSubstring "Cannot implicitly convert type"
