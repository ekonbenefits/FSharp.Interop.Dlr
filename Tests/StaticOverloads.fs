[<ReflectedDefinition>]
module Tests.StaticOverloads

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``a static overload is picked by the argument's runtime type`` () =
    let draw (o: obj) : string = dlr { return Dlr.Static<Renderer>.Overloads?Draw(o) }
    draw (box { X = 1; Y = 2 }) |> should equal "point 1"
    draw (box (Circle 3)) |> should equal "shape 3"
    draw (box 42) |> should equal "obj 42"
    draw (box (Rect(1, 1))) |> should equal "shape rect"           // one site, alternating types

[<Fact>]
let ``typed arguments bind by their static type`` () =
    let p = { X = 7; Y = 0 }
    (dlr { return Dlr.Static<Renderer>.Overloads?Draw(p) } : string) |> should equal "point 7"
    (dlr { return Dlr.Static<Renderer>.Overloads?Draw("s") } : string) |> should equal "obj s"

[<Fact>]
let ``pipe form, computed name, type arguments`` () =
    (dlr { return Dlr.Static<Renderer>.Overloads |> Dlr.invoke "Draw" (box 1) } : string) |> should equal "obj 1"
    let call (m: string) : string = dlr { return (?) Dlr.Static<Renderer>.Overloads m (box 9) }
    call "Draw" |> should equal "obj 9"
    (dlr { return Dlr.Static<Renderer>.Overloads?Parse(Dlr.typeArgs<int>(), "42") } : int) |> should equal 42
    let t = typeof<float>
    (dlr { return Dlr.Static<Renderer>.Overloads?Parse(Dlr.typeArgsOf [ t ], "2.5") } : float) |> should equal 2.5

[<Fact>]
let ``BCL statics, and F# private statics from the same assembly`` () =
    (dlr { return Dlr.Static<Math>.Overloads?Max(box 3, box 7) } : int) |> should equal 7
    (dlr { return Dlr.Static<Math>.Overloads?Max(box 2.5, box 1.0) } : float) |> should equal 2.5
    (dlr { return Dlr.Static<String>.Overloads?Join(", ", [| "a"; "b" |]) } : string) |> should equal "a, b"
#if DLRQ
    // No enclosing member, so the context is obj: an IL-internal member is out of reach.
    (fun () -> (dlr { return Dlr.Static<Renderer>.Overloads?Secret() } : string) |> ignore) |> should throw typeof<RuntimeBinderException>
#else
    (dlr { return Dlr.Static<Renderer>.Overloads?Secret() } : string) |> should equal "secret"   // F# private is IL internal
#endif

[<Fact>]
let ``a miss is the binder's error; anything but a call is a translation error`` () =
    (fun () -> (dlr { return Dlr.Static<Renderer>.Overloads?Nope() } : string) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> (dlr { return Dlr.Static<Renderer>.Overloads?Scale } : float) |> ignore) |> should throw typeof<DlrTranslationException>
    (fun () -> dlr { Dlr.Static<Renderer>.Overloads?Scale <- 2.0 }) |> should throw typeof<DlrTranslationException>
    (fun () -> dlr { Dlr.Static<Renderer>.Overloads |> Dlr.addAssign "Scale" 1.0 }) |> should throw typeof<DlrTranslationException>
    (fun () -> (dlr { return Dlr.Static<Renderer>.Overloads |> Dlr.item 0 } : int) |> ignore) |> should throw typeof<DlrTranslationException>
    (fun () -> (dlr { return Dlr.Static<Renderer>.Overloads?Draw } : obj -> string) |> ignore) |> should throw typeof<DlrTranslationException>
    (fun () -> (dlr { return Dlr.Static<Renderer>.Overloads |> Dlr.call (box 1) } : string) |> ignore) |> should throw typeof<DlrTranslationException>
    // Anywhere but in target position: a translation error, not the outside-a-block one at run time.
    (fun () -> (dlr { let s = Dlr.Static<Renderer>.Overloads in return s?Draw(box 3) } : string) |> ignore) |> should throw typeof<DlrTranslationException>
    (fun () -> (dlr { return (box 1)?Equals(Dlr.Static<Renderer>.Overloads) } : bool) |> ignore) |> should throw typeof<DlrTranslationException>
