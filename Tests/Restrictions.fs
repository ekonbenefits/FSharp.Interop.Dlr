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
let ``F# optional parameters are not optional to the binder`` () =
    let o = box (Widget())
    // ?step compiles to an FSharpOption<int> parameter with no [Optional] metadata: it cannot be omitted...
    (fun () -> (dlr { return o?BumpF(1) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>
    // ...but FSharpOption<'T> has an op_Implicit from 'T, so a bare value converts, as does an explicit Some.
    (dlr { return o?BumpF(1, 2) } : int) |> should equal 3
    (dlr { return o?BumpF(1, Some 2) } : int) |> should equal 3
    // ...while [<Optional; DefaultParameterValue>] is optional, as in C#.
    (dlr { return o?Bump(1) } : int) |> should equal 2
    (dlr { return o?Bump(1, 5) } : int) |> should equal 6
