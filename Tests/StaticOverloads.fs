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
    let point: string = dlr { return Dlr.Static<Renderer>.Overloads?Draw(p) }
    let text: string = dlr { return Dlr.Static<Renderer>.Overloads?Draw("s") }
    point |> should equal "point 7"
    text |> should equal "obj s"

[<Fact>]
let ``pipe form, computed name, type arguments`` () =
    let piped: string = dlr { return Dlr.Static<Renderer>.Overloads |> Dlr.invoke "Draw" (box 1) }
    piped |> should equal "obj 1"
    let call (m: string) : string = dlr { return (?) Dlr.Static<Renderer>.Overloads m (box 9) }
    call "Draw" |> should equal "obj 9"
    let parsed: int = dlr { return Dlr.Static<Renderer>.Overloads?Parse(Dlr.typeArgs<int>(), "42") }
    parsed |> should equal 42
    let t = typeof<float>
    let parsedAs: float = dlr { return Dlr.Static<Renderer>.Overloads?Parse(Dlr.typeArgsOf [ t ], "2.5") }
    parsedAs |> should equal 2.5

[<Fact>]
let ``BCL statics, and F# private statics from the same assembly`` () =
    let max (a: obj) (b: obj) : obj = dlr { return Dlr.Static<Math>.Overloads?Max(a, b) }
    max 3 7 |> should equal (box 7)
    max 2.5 1.0 |> should equal (box 2.5)
    let joined: string = dlr { return Dlr.Static<String>.Overloads?Join(", ", [| "a"; "b" |]) }
    joined |> should equal "a, b"
    let secret: string = dlr { return Dlr.Static<Renderer>.Overloads?Secret() }        // F# private is IL internal
    secret |> should equal "secret"

[<Fact>]
let ``a miss is the binder's error; anything but a call is a translation error`` () =
    (fun () -> (dlr { return Dlr.Static<Renderer>.Overloads?Nope() } : string) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> (dlr { return Dlr.Static<Renderer>.Overloads?Scale } : float) |> ignore) |> should throw typeof<DlrTranslationException>
    (fun () -> dlr { Dlr.Static<Renderer>.Overloads?Scale <- 2.0 }) |> should throw typeof<DlrTranslationException>
    (fun () -> dlr { Dlr.Static<Renderer>.Overloads |> Dlr.addAssign "Scale" 1.0 }) |> should throw typeof<DlrTranslationException>
    (fun () -> (dlr { return Dlr.Static<Renderer>.Overloads |> Dlr.item 0 } : int) |> ignore) |> should throw typeof<DlrTranslationException>
    (fun () -> (dlr { return Dlr.Static<Renderer>.Overloads?Draw } : obj -> string) |> ignore) |> should throw typeof<DlrTranslationException>
    (fun () -> (dlr { return Dlr.Static<Renderer>.Overloads |> Dlr.apply (box 1) } : string) |> ignore) |> should throw typeof<DlrTranslationException>
    // Anywhere but in target position: a translation error, not the outside-a-block one at run time.
    (fun () -> (dlr { let s = Dlr.Static<Renderer>.Overloads in return s?Draw(box 3) } : string) |> ignore) |> should throw typeof<DlrTranslationException>
    (fun () -> (dlr { return (box 1)?Equals(Dlr.Static<Renderer>.Overloads) } : bool) |> ignore) |> should throw typeof<DlrTranslationException>
