[<ReflectedDefinition>]
module Tests.Constructors

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``the constructor overload is picked by the argument's runtime type`` () =
    let make (o: obj) : Handler = dlr { return Dlr.new'<Handler>(o) }
    (make (box { X = 1; Y = 2 })).Kind |> should equal "point"
    (make (box (Circle 3))).Kind |> should equal "shape"
    (make (box 42)).Kind |> should equal "obj"
    (make (box { X = 3; Y = 4 })).Detail |> should equal "3,4"   // one site, alternating types

[<Fact>]
let ``typed, no, named and tupled arguments`` () =
    let p = { X = 5; Y = 6 }
    (dlr { return Dlr.new'<Handler>(p) } : Handler).Kind |> should equal "point"        // static type binds
    (dlr { return Dlr.new'<Handler>() } : Handler).Kind |> should equal "none"
    (dlr { return Dlr.new'<Handler>("k", 2) } : Handler).Detail |> should equal "k:2"
    (dlr { return Dlr.new'<Handler>(Dlr.named {| count = 7; name = "n" |}) } : Handler).Detail |> should equal "n:7"
    (dlr { return Dlr.new'<System.Text.StringBuilder>("seed") } : System.Text.StringBuilder).ToString() |> should equal "seed"
    let n: int = dlr { return Dlr.new'<Widget>()?Count }
    n |> should equal 3

[<Fact>]
let ``no matching constructor is a binder error`` () =
    (fun () -> (dlr { return Dlr.new'<Handler>(1, 2, 3) } : Handler) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> (dlr { return Dlr.new'<Handler>(box 1, "x") } : Handler) |> ignore) |> should throw typeof<RuntimeBinderException>
