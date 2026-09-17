/// One test per bullet of the README's "The same restrictions as C# dynamic".
[<ReflectedDefinition>]
module Tests.Restrictions

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``extension methods are not found`` () =
    let w = Widget()
    WidgetExtensions.Twice w |> should equal 6   // it exists, statically
    let o = box w
    (fun () -> (dlr { return o?Twice() } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``static members cannot be reached through an instance`` () =
    let o = box (Widget())
    (fun () -> (dlr { return o?Make() } : obj) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``explicitly implemented interface members are not found`` () =
    let g = box (Greeter())
    (dlr { return g?Name } : string) |> should equal "greeter"                 // a public member binds
    (fun () -> (dlr { return g?Greet("you") } : string) |> ignore) |> should throw typeof<RuntimeBinderException>
    // The way through: a static cast to the interface.
    (g :?> IGreeter).Greet "you" |> should equal "hello you"

[<Fact>]
let ``accessibility is the calling type's`` () =
    // Widget.Secret is `member private`, which is IL internal: reachable from this assembly...
    let w = Widget()
    (dlr { return (box w)?Secret } : string) |> should equal "hidden"
    // ...while a genuinely private member of another assembly is not.
    let s = box "abc"
    (fun () -> (dlr { return s?_firstChar } : char) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``lambdas passed as arguments need a delegate type`` () =
    let o = box (Widget())
    let asFunction = fun (x: int) -> x * 2          // an FSharpFunc object: not a Func<int,int>
    (fun () -> (dlr { return o?Run(asFunction) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>
    let asDelegate = Func<int, int>(fun x -> x * 2)
    (dlr { return o?Run(asDelegate) } : int) |> should equal 42

[<Fact>]
let ``no compile-time checking: the miss is at the call`` () =
    let o = box (Widget())
    let attempt () = (dlr { return o?Cuont } : int) |> ignore     // misspelt member compiles fine
    attempt |> should throw typeof<RuntimeBinderException>
    (fun () -> (dlr { return o?Add(1) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>   // wrong arity

[<Fact>]
let ``the result is obj so a value type comes back boxed; arguments keep their static type`` () =
    let o = box (Widget())
    let boxed: obj = dlr { return o?Count }
    boxed |> should be instanceOfType<int>
    // Pick(int) vs Pick(obj): the static int type wins, the same argument through obj dispatches on the runtime type.
    let n = 5
    (dlr { return o?Pick(n) } : string) |> should equal "int"
    (dlr { return o?Pick(box n) } : string) |> should equal "int"
    let s = "5"
    (dlr { return o?Pick(s) } : string) |> should equal "string"

[<Fact>]
let ``generic type arguments must be inferable or given`` () =
    let o = box (Widget())
    (dlr { return o?Echo(41) } : int) |> should equal 41                                   // inferred from the argument
    (fun () -> (dlr { return o?Default() } : int) |> ignore) |> should throw typeof<RuntimeBinderException>   // return-only type parameter
    (dlr { return o?Default(Dlr.typeArgs<int>()) } : int) |> should equal 0

[<Fact>]
let ``a failed bind is a RuntimeBinderException for every kind of miss`` () =
    let o = box (Widget())
    let d = box 3.9
    (fun () -> (dlr { return o?Nope } : obj) |> ignore) |> should throw typeof<RuntimeBinderException>          // no member
    (fun () -> (dlr { return o?Add("a", "b") } : int) |> ignore) |> should throw typeof<RuntimeBinderException> // no overload
    (fun () -> (dlr { return Dlr.implicit d } : int) |> ignore) |> should throw typeof<RuntimeBinderException>  // no implicit conversion

[<Fact>]
let ``F# optional parameters can be omitted, the library binding what C# cannot`` () =
    let w = Widget()
    let o = box w
    // ?step is an FSharpOption<int> parameter with no [Optional] metadata: C#'s binder cannot omit
    // it, so the library offers its own rule for that case (None for the omitted, Some for a bare value).
    (dlr { return o?BumpF(1) } : int) |> should equal 2
    (dlr { return o?BumpF(1, 2) } : int) |> should equal 3         // C#'s own path: op_Implicit to Some
    (dlr { return o?BumpF(1, Some 5) } : int) |> should equal 6
    (dlr { return o?Wrap() } : string) |> should equal "<x>"
    (dlr { return o?Wrap("[") } : string) |> should equal "[x>"
    (dlr { return o?Wrap("[", "]") } : string) |> should equal "[x]"
    dlr { o?TouchF() }
    dlr { o?TouchF(2) }
    w.Touched |> should equal 3
    // The library's rule accepts what C# would for the required slots: numeric widening, a null.
    (dlr { return o?WidenF(5) } : int64) |> should equal 5L
    (dlr { return o?WidenF(5, Some 2) } : int64) |> should equal 10L
    (dlr { return o?WidenF(5, 3) } : int64) |> should equal 15L
    (dlr { return o?LabelF(null) } : string) |> should equal "null"
    (dlr { return o?LabelF(box null) } : string) |> should equal "null"
    (dlr { return o?LabelF(box "s") } : string) |> should equal "s"
    (fun () -> (dlr { return o?LabelF(box 5) } : string) |> ignore) |> should throw typeof<RuntimeBinderException>
    let bump: int -> int = dlr { return o?BumpF }              // bound with the optional omitted
    bump 10 |> should equal 11
    // [<Optional; DefaultParameterValue>] parameters are C#'s own optional and were always fine.
    (dlr { return o?Bump(1) } : int) |> should equal 2

