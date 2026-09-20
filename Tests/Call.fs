/// `Dlr.call`, the `?` of values: applied it invokes the target; read at a function type it is that function.
[<ReflectedDefinition>]
module Tests.Call

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``Dlr.call read as a function type invokes the target: delegate, F# function, TryInvoke object`` () =
    let d = box (Func<int, int, int>(fun a b -> a + b))
    let add: int -> int -> int = dlr { return Dlr.call d }
    add 1 2 |> should equal 3
    let addOne = add 1                                        // partial application
    addOne 41 |> should equal 42
    let tupled: int * int -> int = dlr { return Dlr.call d }
    tupled (2, 3) |> should equal 5
    let thunk = box (Func<string>(fun () -> "thunk"))
    let t: unit -> string = dlr { return Dlr.call thunk }
    t () |> should equal "thunk"
    let r = Recorder()
    let o = box r
    let viaTryInvoke: int -> string = dlr { return Dlr.call o }
    viaTryInvoke 5 |> should equal "5"
    List.ofSeq r.Log |> should equal [ "invoke self(1 args)" ]

[<Fact>]
let ``Dlr.call read at exactly the function's type returns the function itself`` () =
    let f = fun (x: int) -> x * 2
    let g: int -> int = dlr { return Dlr.call (box f) }
    obj.ReferenceEquals(f, g) |> should equal true
    // Another type: an invoker over it, still correct.
    let h: int -> int64 = dlr { return Dlr.call (box f) }
    h 21 |> should equal 42L

[<Fact>]
let ``Dlr.call applied is a call, and piped it reads`` () =
    let d = box (Func<int, int, int>(fun a b -> a + b))
    let called: int = dlr { return Dlr.call d (1, 2) }
    let pipedThenApplied: int = dlr { return (d |> Dlr.call) (4, 5) }
    called |> should equal 3
    pipedThenApplied |> should equal 9
    let f: int * int -> int = dlr { return d |> Dlr.call }
    f (6, 7) |> should equal 13

[<Fact>]
let ``Dlr.call read as a function is lazy: a non-callable fails at the first application`` () =
    let s = box "text"
    let f: int -> int = dlr { return Dlr.call s }
    (fun () -> f 1 |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``Dlr.call read at a non-function type is a translation error naming Dlr.implicit`` () =
    let d = box 1
    // The analyzer reports this at build time (DLR005); this pins the run-time error behind it.
    // fsharpanalyzer: ignore-line-next DLR005
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> (dlr { return Dlr.call d } : int) |> ignore)
    ex.Message |> should haveSubstring "Dlr.implicit"
