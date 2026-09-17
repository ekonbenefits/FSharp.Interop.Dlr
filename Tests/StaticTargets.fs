[<ReflectedDefinition>]
module Tests.StaticTargets

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``a static overload is picked by the argument's runtime type`` () =
    let draw (o: obj) : string = dlr { return Dlr.static'<Renderer>()?Draw(o) }
    draw (box { X = 1; Y = 2 }) |> should equal "point 1"
    draw (box (Circle 3)) |> should equal "shape 3"
    draw (box 42) |> should equal "obj 42"
    draw (box (Rect(1, 1))) |> should equal "shape rect"           // one site, alternating types

[<Fact>]
let ``typed arguments bind by their static type`` () =
    let p = { X = 7; Y = 0 }
    (dlr { return Dlr.static'<Renderer>()?Draw(p) } : string) |> should equal "point 7"
    (dlr { return Dlr.static'<Renderer>()?Draw("s") } : string) |> should equal "obj s"

[<Fact>]
let ``static property get and set, and pipe forms`` () =
    Renderer.Scale <- 1.0
    (dlr { return Dlr.static'<Renderer>()?Scale } : float) |> should equal 1.0
    dlr { Dlr.static'<Renderer>()?Scale <- 2.5 }
    Renderer.Scale |> should equal 2.5
    (dlr { return Dlr.static'<Renderer>() |> Dlr.get "Scale" } : float) |> should equal 2.5
    dlr { Dlr.static'<Renderer>() |> Dlr.set "Scale" 4.0 }
    Renderer.Scale |> should equal 4.0
    (dlr { return Dlr.static'<Renderer>() |> Dlr.invoke "Draw" (box 1) } : string) |> should equal "obj 1"
    dlr { Dlr.static'<Renderer>() |> Dlr.addAssign "Scale" 1.0 }
    Renderer.Scale |> should equal 5.0

[<Fact>]
let ``generic static with explicit type arguments`` () =
    (dlr { return Dlr.static'<Renderer>()?Parse(Dlr.typeArgs<int>(), "42") } : int) |> should equal 42
    let t = typeof<float>
    (dlr { return Dlr.static'<Renderer>()?Parse(Dlr.typeArgsOf [ t ], "2.5") } : float) |> should equal 2.5

[<Fact>]
let ``computed member name on a static target`` () =
    let call (m: string) : string = dlr { return (?) (Dlr.static'<Renderer>()) m (box 9) }
    call "Draw" |> should equal "obj 9"

[<Fact>]
let ``static events with addAssign and subtractAssign`` () =
    let seen = ResizeArray<int>()
    let h = Handler<int>(fun _ n -> seen.Add n)
    dlr { Dlr.static'<Renderer>() |> Dlr.addAssign "Changed" h }
    Renderer.Raise 1
    dlr { Dlr.static'<Renderer>() |> Dlr.subtractAssign "Changed" h }
    Renderer.Raise 2
    List.ofSeq seen |> should equal [ 1 ]

[<Fact>]
let ``misses are binder errors, and BCL statics work`` () =
    (fun () -> (dlr { return Dlr.static'<Renderer>()?Nope() } : string) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> (dlr { return Dlr.static'<Renderer>()?Nope } : string) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> dlr { Dlr.static'<Renderer>()?Nope <- 1 }) |> should throw typeof<RuntimeBinderException>
    (dlr { return Dlr.static'<Math>()?Max(box 3, box 7) } : int) |> should equal 7
    (dlr { return Dlr.static'<Math>()?Max(box 2.5, box 1.0) } : float) |> should equal 2.5
    (dlr { return Dlr.static'<String>()?Join(", ", [| "a"; "b" |]) } : string) |> should equal "a, b"
    (dlr { return Dlr.static'<String>()?Empty } : string) |> should equal ""                  // a static field
    (dlr { return Dlr.static'<Int32>()?MaxValue } : int) |> should equal Int32.MaxValue
    // F# `private` is IL internal: reachable from anywhere in the assembly, as documented.
    (dlr { return Dlr.static'<Renderer>()?Secret() } : string) |> should equal "secret"
    (fun () -> dlr { Dlr.static'<String>()?Empty <- "x" }) |> should throw typeof<RuntimeBinderException>   // read-only
