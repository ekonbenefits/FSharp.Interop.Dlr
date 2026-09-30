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
let ``anything but a supported form is a translation error`` () =
    let o = box (ByRefs())
    let name = "TryHalf"
    // The analyzer reports each of these at build time (DLR005); this pins the run-time error behind it.
    // fsharpanalyzer: ignore-region-start DLR005
    (fun () -> dlr { o?Twice(Dlr.ref "x") }) |> should throw typeof<DlrTranslationException>                                        // not a mutable
    (fun () -> (dlr { return box Dlr.out } : obj) |> ignore) |> should throw typeof<DlrTranslationException>                      // not an argument
    (fun () -> (dlr { return Dlr.call o (6, Dlr.out) } : bool * int) |> ignore) |> should throw typeof<DlrTranslationException>                  // a value call: not yet
    (fun () -> (dlr { return ((?) o name) (6, Dlr.out) } : bool * int) |> ignore) |> should throw typeof<DlrTranslationException>              // computed name
    (fun () -> (dlr { return o?TryHalf(6, Dlr.out) } : bool * int * int) |> ignore) |> should throw typeof<DlrTranslationException>            // shape
    // fsharpanalyzer: ignore-region-end DLR005
