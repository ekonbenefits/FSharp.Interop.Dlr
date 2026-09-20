[<ReflectedDefinition>]
module Tests.Constructors

open System
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
    let typed: Handler = dlr { return Dlr.new'<Handler>(p) }                                   // static type binds
    let none: Handler = dlr { return Dlr.new'<Handler>() }
    let tupled: Handler = dlr { return Dlr.new'<Handler>("k", 2) }
    let named: Handler = dlr { return Dlr.new'<Handler>(Dlr.named {| count = 7; name = "n" |}) }
    typed.Kind |> should equal "point"
    none.Kind |> should equal "none"
    tupled.Detail |> should equal "k:2"
    named.Detail |> should equal "n:7"
    let sb: System.Text.StringBuilder = dlr { return Dlr.new'<System.Text.StringBuilder>("seed") }
    sb.ToString() |> should equal "seed"
    let count: int = dlr { return Dlr.new'<Widget>()?Count }
    count |> should equal 3

[<Fact>]
let ``a tuple in a variable is several arguments, as for a member call`` () =
    let args = ("k", 2)
    let h: Handler = dlr { return Dlr.new'<Handler> args }
    h.Detail |> should equal "k:2"
    let make (args: string * int) : Handler = dlr { return Dlr.new'<Handler> args }
    (make ("m", 3)).Detail |> should equal "m:3"
    // `box t` passes the tuple as one argument, as for a member call: here the obj constructor.
    let boxed: Handler = dlr { return Dlr.new'<Handler>(box args) }
    boxed.Kind |> should equal "obj"

[<Fact>]
let ``no matching constructor is a binder error`` () =
    (fun () -> (dlr { return Dlr.new'<Handler>(1, 2, 3) } : Handler) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> (dlr { return Dlr.new'<Handler>(box 1, "x") } : Handler) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``value types construct too`` () =
    // The constructor site is typed T, as C#'s own is; an obj-typed site rejected a struct result.
    (dlr { return Dlr.new'<DateTime>(2020, 1, 2) } : DateTime) |> should equal (DateTime(2020, 1, 2))
    (dlr { return Dlr.new'<TimeSpan>(box 1, box 2, box 3) } : TimeSpan) |> should equal (TimeSpan(1, 2, 3))
    (dlr { return Dlr.new'<Guid>("00000000-0000-0000-0000-000000000001") } : Guid).ToString().EndsWith "1" |> should equal true
