/// The rules that go beyond C# — omitting F# optional parameters, an F# function for a delegate
/// parameter, a delegate for a function parameter — apply to every kind of call, not only
/// instance methods: static methods (`Dlr.Static<T>.Overloads`), constructors (`Dlr.new'`),
/// delegate-typed members, an Expando's delegate member, `Dlr.apply` on a delegate.
[<ReflectedDefinition>]
module Tests.RulesEverywhere

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``static methods: optional parameters omitted, F# function for a Func, delegate for a function`` () =
    (dlr { return Dlr.Static<Statics>.Overloads?BumpF(1) } : int) |> should equal 2
    (dlr { return Dlr.Static<Statics>.Overloads?BumpF(1, 5) } : int) |> should equal 6
    (dlr { return Dlr.Static<Statics>.Overloads?Run(fun (x: int) -> x * 2) } : int) |> should equal 42
    (dlr { return Dlr.Static<Statics>.Overloads?Apply(20, Func<int, int>(fun x -> x + 1)) } : int) |> should equal 21
    (dlr { return Dlr.Static<Statics>.Overloads |> Dlr.invoke "BumpF" 1 } : int) |> should equal 2
    (fun () -> (dlr { return Dlr.Static<Statics>.Overloads?BumpF() } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``constructors: optional parameters omitted, F# function for a Func, delegate for a function`` () =
    (dlr { return Dlr.new'<Ctor>(1) } : Ctor).Value |> should equal 2
    (dlr { return Dlr.new'<Ctor>(1, 5) } : Ctor).Value |> should equal 6
    (dlr { return Dlr.new'<CtorF>(fun (x: int) -> x * 2) } : CtorF).Value |> should equal 42
    (dlr { return Dlr.new'<CtorFn>(Func<int, int>(fun x -> x + 1)) } : CtorFn).Value |> should equal 22
    (fun () -> (dlr { return Dlr.new'<CtorF>("no") } : CtorF) |> ignore) |> should throw typeof<RuntimeBinderException>
    (dlr { return Dlr.new'<DateTime>(2020, 1, 2) } : DateTime).Year |> should equal 2020   // C#'s own path untouched

[<Fact>]
let ``delegate members and delegate values take F# functions`` () =
    let o = box (DelegateMembers())
    (dlr { return o?Run(fun (x: int) -> x * 2) } : int) |> should equal 42                 // a Func<Func<int,int>,int> member
    (dlr { return o?Apply(Func<int, int>(fun x -> x + 1)) } : int) |> should equal 22       // an F# function member taking a function, given a delegate
    let e = box (Fixtures.expando [ "Run", box (Func<Func<int, int>, int>(fun f -> f.Invoke 21)) ])
    (dlr { return e?Run(fun (x: int) -> x * 2) } : int) |> should equal 42                 // an Expando's delegate member
    let d = box (Func<Func<int, int>, int>(fun f -> f.Invoke 21))
    (dlr { return d |> Dlr.apply (fun (x: int) -> x * 2) } : int) |> should equal 42        // Dlr.apply on a delegate
    let optionalDelegate = box (Func<int, int>(fun x -> x + 1))
    (dlr { return optionalDelegate |> Dlr.apply 20 } : int) |> should equal 21              // C#'s own invoke still first
