[<ReflectedDefinition>]
module Tests.ByRef

open System
open System.Collections.Generic
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Tests.CSharp

[<Fact>]
let ``TryGetValue with Dlr.out: found and missing`` () =
    let d = box (Dictionary<string, int>(dict [ "a", 1 ]))
    let (found: bool), (v: int) = dlr { return d?TryGetValue("a", Dlr.out) }
    let (missing: bool), (w: int) = dlr { return d?TryGetValue("z", Dlr.out) }
    (found, v) |> should equal (true, 1)
    (missing, w) |> should equal (false, 0)

[<Fact>]
let ``a return value and an out`` () =
    let o = box (ByRefs())
    let (q: int), (r: int) = dlr { return o?DivRem(7, 2, Dlr.out) }
    (q, r) |> should equal (3, 1)

[<Fact>]
let ``a void method's outs are the result`` () =
    let o = box (ByRefs())
    let (left: string), (right: string) = dlr { return o?Split("a,b", Dlr.out, Dlr.out) }
    (left, right) |> should equal ("a", "b")

[<Fact>]
let ``Dlr.ref writes back to a let mutable`` () =
    let o = box (ByRefs())
    let mutable a = 1
    let mutable b = 2
    dlr { o?Swap(Dlr.ref a, Dlr.ref b) }
    (a, b) |> should equal (2, 1)

[<Fact>]
let ``Dlr.ref on a mutable declared in the block, and inside a loop`` () =
    let o = box (ByRefs())
    let result: string =
        dlr {
            let mutable s = "x"
            for _ in 1 .. 3 do
                o?Twice(Dlr.ref s)
            return s
        }
    result |> should equal "xxxxxxxx"

[<Fact>]
let ``TryParse through Dlr.Static<T>.Overloads`` () =
    let text = box "42"
    let (ok: bool), (n: int) = dlr { return Dlr.Static<Int32>.Overloads?TryParse(text, Dlr.out) }
    (ok, n) |> should equal (true, 42)

[<Fact>]
let ``a dynamic object's out value comes back`` () =
    let o = box (DynamicOuts())
    let (even: bool), (half: obj) = dlr { return o?TryHalf(8, Dlr.out) }
    (even, half) |> should equal (true, box 4)

[<Fact>]
let ``one site across a CLR object and a dynamic object`` () =
    let targets: obj list = [ box (ByRefs()); box (DynamicOuts()) ]
    let results = ResizeArray<bool * int>()
    for t in targets do
        let (even: bool), (half: int) = dlr { return t?TryHalf(6, Dlr.out) }
        results.Add((even, half))
    List.ofSeq results |> should equal [ (true, 3); (true, 3) ]

[<Fact>]
let ``an out whose type does not match the parameter is the binder's error, as in C#`` () =
    // C# requires an out argument's type to be the parameter's exactly: `out object` does not
    // bind `out int`, and a miss arrives as a RuntimeBinderException, not wrapped.
    let o = box (ByRefs())
    (fun () -> (dlr { return o?TryHalf(6, Dlr.out) } : bool * obj) |> ignore)
    |> should throw typeof<Microsoft.CSharp.RuntimeBinder.RuntimeBinderException>

[<Fact>]
let ``Dlr.invoke and an applied Dlr.get take Dlr.out as x?M(…) does`` () =
    let o = box (ByRefs())
    let (even: bool), (half: int) = dlr { return o |> Dlr.invoke "TryHalf" (6, Dlr.out) }
    let (q: int), (r: int) = dlr { return (o |> Dlr.get "DivRem") (7, 2, Dlr.out) }
    (even, half, q, r) |> should equal (true, 3, 3, 1)

[<Fact>]
let ``a computed member name`` () =
    let o = box (ByRefs())
    let call (name: string) : bool * int = dlr { return ((?) o name) (6, Dlr.out) }
    call "TryHalf" |> should equal (true, 3)

[<Fact>]
let ``type arguments, static and from data`` () =
    let o = box (Generic())
    let (ok: bool), (v: int) = dlr { return o?TryDefault(Dlr.typeArgs<int>(), Dlr.out) }
    let types = [ typeof<string> ]
    let (ok2: bool), (s: string) = dlr { return o?TryDefault(Dlr.typeArgsOf types, Dlr.out) }
    let valueTypes = [ typeof<int> ]
    let (ok3: bool), (n: int) = dlr { return o?TryDefault(Dlr.typeArgsOf valueTypes, Dlr.out) }   // a value-type out through a per-key site
    (ok, v, ok2, isNull s, ok3, n) |> should equal (true, 0, true, true, true, 0)

[<Fact>]
let ``a named argument beside an out`` () =
    let o = box (ByRefs())
    let (q: int), (r: int) = dlr { return o?Scale(7, Dlr.out, Dlr.named {| by = 3 |}) }
    (q, r) |> should equal (2, 1)

[<Fact>]
let ``Dlr.call and Dlr.apply invoke a delegate with an out`` () =
    let f = box ByRefs.HalfFn
    let (even: bool), (half: int) = dlr { return Dlr.call f (6, Dlr.out) }
    let (odd: bool), (half2: int) = dlr { return f |> Dlr.apply (7, Dlr.out) }
    (even, half, odd, half2) |> should equal (true, 3, false, 3)

[<Fact>]
let ``an internal delegate invoked with an out or a ref`` () =
    // Its Invoke is internal: on .NET Framework C#'s Invoke binder cannot invoke it, ours does.
    let tryHalf = box InternalByRefs.TryHalf
    let (even: bool), (half: int) = dlr { return Dlr.call tryHalf (8, Dlr.out) }
    (even, half) |> should equal (true, 4)
    let (odd: bool), (half2: int) = dlr { return tryHalf |> Dlr.apply (9, Dlr.out) }
    (odd, half2) |> should equal (false, 4)
    let bump = box InternalByRefs.Bump
    let mutable n = 40
    dlr { Dlr.call bump (Dlr.ref n, 2) }
    n |> should equal 42
    // Named arguments match by name, not position; a wrong name is C#'s error.
    let named = box InternalByRefs.Named
    let (d: int), (r: int) = dlr { return Dlr.call named (Dlr.out, Dlr.named {| a = 1; x = 10 |}) }
    (d, r) |> should equal (9, 1001)
    (fun () -> (dlr { return Dlr.call named (Dlr.out, Dlr.named {| zz = 1; a = 10 |}) } : int * int) |> ignore)
    |> should throw typeof<Microsoft.CSharp.RuntimeBinder.RuntimeBinderException>
    // An int for an int64 parameter widens, as C# does.
    let wide = box InternalByRefs.Wide
    let (evenW: bool), (halfW: int64) = dlr { return Dlr.call wide (8, Dlr.out) }
    (evenW, halfW) |> should equal (true, 4L)
    // A void delegate: its outs alone when the result is discarded; its result used is C#'s error.
    let voidOut = box InternalByRefs.VoidOut
    let twice: int = dlr { return Dlr.call voidOut (21, Dlr.out) }
    twice |> should equal 42
    // A ref for an out, an out for a ref, a ref for a plain parameter, an unknown name beside a
    // ref: C#'s errors, which our rule (on .NET Framework) must not bind past.
    let mutable m = 0
    let mutable step = 2
    let refused (f: unit -> unit) = f |> should throw typeof<Microsoft.CSharp.RuntimeBinder.RuntimeBinderException>
    refused (fun () -> (dlr { return Dlr.call bump (Dlr.out, 2) } : int) |> ignore)
    refused (fun () -> (dlr { return Dlr.call tryHalf (8, Dlr.ref m) } : bool) |> ignore)
    refused (fun () -> dlr { Dlr.call bump (Dlr.ref n, Dlr.ref step) })
    refused (fun () -> dlr { Dlr.call bump (Dlr.ref n, Dlr.named {| nonexistent = 2 |}) })
    n |> should equal 42

[<Fact>]
let ``Dlr.new' with a constructor ref`` () =
    // Dlr.new'<T> returns T, so a constructor's out has no room in the result; a ref writes back.
    let mutable count = 0
    let made: Counted = dlr { return Dlr.new'<Counted>("a", Dlr.ref count) }
    let again: Counted = dlr { return Dlr.new'<Counted>("b", Dlr.ref count) }
    (made.Name, again.Name, count) |> should equal ("a", "b", 2)

[<Fact>]
let ``anything but a supported form is a translation error`` () =
    let o = box (ByRefs())
    // The analyzer reports each of these at build time (DLR005); this pins the run-time error behind it.
    // fsharpanalyzer: ignore-region-start DLR005
    (fun () -> dlr { o?Twice(Dlr.ref "x") }) |> should throw typeof<DlrTranslationException>                                        // not a mutable
    (fun () -> (dlr { return box Dlr.out } : obj) |> ignore) |> should throw typeof<DlrTranslationException>                      // not an argument
    (fun () -> (dlr { return o?TryHalf(Dlr.out, Dlr.namedOf [ "n", box 6 ]) } : bool * int) |> ignore) |> should throw typeof<DlrTranslationException>   // with a splat
    (fun () -> (dlr { return Dlr.new'<Counted>("a", Dlr.out) } : Counted) |> ignore) |> should throw typeof<DlrTranslationException>              // new' returns T: no room for an out
    (fun () -> (dlr { return o?TryHalf(6, Dlr.out) } : struct (bool * int * int)) |> ignore) |> should throw typeof<DlrTranslationException>   // shape, a struct tuple
    (fun () -> (dlr { return o?TryHalf(6, Dlr.out) } : unit)) |> should throw typeof<DlrTranslationException>                                  // shape, unit: no slot for the out
    (fun () -> (dlr { return o?TryHalf(6, Dlr.outAs<string> ()) } : bool * int) |> ignore) |> should throw typeof<DlrTranslationException>      // a stated type no shape agrees with
    (fun () -> (dlr { return o?Halve(6, Dlr.out) } : System.ValueTuple<int>) |> ignore) |> should throw typeof<DlrTranslationException>      // a one-element tuple: not a shape (was an internal crash)
    (fun () -> (dlr { return o?TryHalf(6, Dlr.out) } : bool * int * int) |> ignore) |> should throw typeof<DlrTranslationException>            // shape
    // fsharpanalyzer: ignore-region-end DLR005

[<Fact>]
let ``byref calls keep C#'s order: the target, then the arguments left to right, a ref read at its place`` () =
    let log = ResizeArray<string>()
    let t () = log.Add "target"; box (Recorder())
    let p () = log.Add "p"; 1
    let n (name: string) = log.Add name; 2
    let orderOf (run: unit -> unit) = log.Clear(); run (); List.ofSeq log
    orderOf (fun () -> (dlr { return (t ())?Call(p (), Dlr.out, Dlr.named {| second = n "second"; first = n "first" |}) } : string * int) |> ignore)
    |> should equal [ "target"; "p"; "second"; "first" ]
    let name () = log.Add "name"; "Call"
    orderOf (fun () -> (dlr { return ((?) (t ()) (name ())) (p (), Dlr.out) } : string * int) |> ignore)
    |> should equal [ "target"; "name"; "p" ]
    // A ref is read where it stands: an earlier argument's write to it is passed in, as in C#.
    let o = box (ByRefs())
    let mutable s = "a"
    let bump () = s <- s + "b"; 1
    dlr { o?Concat(bump (), Dlr.ref s) }
    s |> should equal "ab1"
    let concat = "Concat"
    s <- "a"
    dlr { ((?) o concat) (bump (), Dlr.ref s) }
    s |> should equal "ab1"
    // And a later argument's write is seen too, as C#'s reference sees it: the value is read at the
    // call, after every other argument.
    s <- "a"
    dlr { o?Prepend(Dlr.ref s, bump ()) }
    s |> should equal (ByRefs.CSharpRefThenWrite o)
    s |> should equal "ab1"

[<Fact>]
let ``a struct (obj * T) result of a computed-name call converts through the binder`` () =
    // The byref holder is a ValueTuple<obj, …>; a user's own struct tuple result must not be taken for one.
    let o = box (ByRefs())
    let name = "Nothing"
    (fun () -> (dlr { return ((?) o name) () } : struct (obj * int)) |> ignore)
    |> should throw typeof<Microsoft.CSharp.RuntimeBinder.RuntimeBinderException>

[<Fact>]
let ``a void method's one out is the bare value`` () =
    let o = box (ByRefs())
    let half: int = dlr { return o?Halve(9, Dlr.out) }
    half |> should equal 4

[<Fact>]
let ``eight outs: past the holder's seven fields`` () =
    let o = box (ByRefs())
    let (a: int), (b: int), (c: int), (d: int), (e: int), (f: int), (g: int), (h: int) =
        dlr { return o?Eight(Dlr.out, Dlr.out, Dlr.out, Dlr.out, Dlr.out, Dlr.out, Dlr.out, Dlr.out) }
    [ a; b; c; d; e; f; g; h ] |> should equal [ 1 .. 8 ]

[<Fact>]
let ``the same variable by ref twice is one storage, as in C#`` () =
    let o = box (ByRefs())
    let mutable x = 0
    let r: int = dlr { return o?AddBoth(Dlr.ref x, Dlr.ref x) }
    struct (r, x) |> should equal (ByRefs.CSharpSameRefTwice o)          // C#'s ValueTuple
    (r, x) |> should equal (4, 2)

[<Fact>]
let ``a struct tuple result: the same shapes, no tuple allocated`` () =
    let d = box (Dictionary<string, int>(dict [ "a", 1 ]))
    let o = box (ByRefs())
    let struct (found: bool, v: int) = dlr { return d?TryGetValue("a", Dlr.out) }
    let struct (q: int, r: int) = dlr { return o?DivRem(7, 2, Dlr.out) }
    let struct (left: string, right: string) = dlr { return o?Split("a,b", Dlr.out, Dlr.out) }
    (found, v, q, r, left, right) |> should equal (true, 1, 3, 1, "a", "b")

[<Fact>]
let ``eight outs into a struct tuple: past ValueTuple's seven fields`` () =
    let o = box (ByRefs())
    let struct (a: int, b: int, c: int, d: int, e: int, f: int, g: int, h: int) =
        dlr { return o?Eight(Dlr.out, Dlr.out, Dlr.out, Dlr.out, Dlr.out, Dlr.out, Dlr.out, Dlr.out) }
    [ a; b; c; d; e; f; g; h ] |> should equal [ 1 .. 8 ]

[<Fact>]
let ``Dlr.outAs states an out's type: a lone tuple-typed out read as the bare value`` () =
    // With Dlr.out a two-element result and one out is the return value then the out; a void
    // method's only out that is itself a pair has no other spelling. Stating the type picks the shape.
    let o = box (ByRefs())
    let v: struct (int * int) = dlr { return o?PairOut(Dlr.outAs<struct (int * int)> ()) }
    let r: int * int = dlr { return o?RefPairOut(Dlr.outAs<int * int> ()) }
    let struct (ok: bool, p: struct (int * int)) = dlr { return o?TryPair(Dlr.outAs<struct (int * int)> ()) }   // beside a return value
    let name = "PairOut"
    let keyed: struct (int * int) = dlr { return ((?) o name) (Dlr.outAs<struct (int * int)> ()) }               // a per-key site
    let d = box (Dictionary<string, int>(dict [ "a", 1 ]))
    let (found: bool), (n: int) = dlr { return d?TryGetValue("a", Dlr.outAs<int> ()) }                           // a plain type: as Dlr.out
    (v, r, ok, p, keyed, found, n) |> should equal (struct (1, 2), (5, 6), true, struct (3, 4), struct (1, 2), true, 1)
    // The piped forms, whose typed-tree node is the function still awaiting its target (the analyzer
    // once reported these at build time while they ran correctly).
    let piped: struct (int * int) = dlr { return o |> Dlr.invoke "PairOut" (Dlr.outAs<struct (int * int)> ()) }
    let f = box ByRefs.HalfFn
    let applied: bool * int = dlr { return f |> Dlr.apply (8, Dlr.outAs<int> ()) }
    (piped, applied) |> should equal (struct (1, 2), (true, 4))

