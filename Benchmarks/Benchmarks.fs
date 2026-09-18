/// The library's hot paths against their alternatives. Every `dlr { }` here is bound and compiled
/// during BenchmarkDotNet's warm-up, so the numbers are the steady-state per-call cost: the block
/// delegate, the sites' cached rules, and whatever the operation itself does.
namespace FSharp.Interop.Dlr.Benchmarks

open System
open System.Reflection
open BenchmarkDotNet.Attributes
open FSharp.Interop.Dlr
open Newtonsoft.Json.Linq

type Widget() =
    member val Count = 3 with get, set
    member val Name = "widget" with get, set
    member _.Add(a: int, b: int) = a + b
    member _.Bump(count: int, ?step: int) = count + defaultArg step 1
    member _.Run(f: Func<int, int>) = f.Invoke 21
    member val Fn = (fun (a: int) (b: int) -> a + b) with get
    static member Draw(_: obj) = 1
    static member Draw(_: Widget) = 2

type Point = { X: int; Y: int }

[<ReflectedDefinition>]
[<MemoryDiagnoser>]
type Core() =
    let w = Widget()
    let o = box w
    let items = [ 1 .. 100 ]
    let countProperty = typeof<Widget>.GetProperty("Count")
    let addMethod = typeof<Widget>.GetMethod("Add")
    let names = [| "Count"; "Name" |]
    let mutable i = 0

    // --- baselines -----------------------------------------------------------------------
    [<Benchmark(Description = "static w.Count")>]
    member _.StaticGet() = w.Count

    // The baseline: the JIT folds the property read to nothing, a call it cannot.
    [<Benchmark(Baseline = true, Description = "static w.Add(i, 1)")>]
    member _.StaticCall() = i <- i + 1; w.Add(i, 1)

    [<Benchmark(Description = "reflection: cached PropertyInfo.GetValue")>]
    member _.ReflectionGet() = countProperty.GetValue(w) :?> int

    [<Benchmark(Description = "reflection: cached MethodInfo.Invoke")>]
    member _.ReflectionCall() = i <- i + 1; addMethod.Invoke(w, [| box i; box 1 |]) :?> int

    [<Benchmark(Description = "FSharp.Interop.Dynamic w?Count")>]
    member _.DynamicGet() = (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o "Count" : int)

    [<Benchmark(Description = "FSharp.Interop.Dynamic w?Add(i, 1)")>]
    member _.DynamicCall() = i <- i + 1; (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o "Add" (i, 1) : int)

    // --- dlr { } -------------------------------------------------------------------------
    [<Benchmark(Description = "dlr w?Count")>]
    member _.Get() : int = dlr { return o?Count }

    [<Benchmark(Description = "dlr w?Add(i, 1)")>]
    member _.Call() : int = i <- i + 1; dlr { return o?Add(i, 1) }

    [<Benchmark(Description = "dlr w?Name <- v")>]
    member _.Set() = dlr { o?Name <- "n" }

    [<Benchmark(Description = "dlr for over 100 items, one site")>]
    member _.Loop() : int =
        dlr {
            let mutable s = 0
            for x in items do s <- s + (o?Add(x, 1) : int)
            return s
        }

    [<Benchmark(Description = "dlr (?) o name, name alternating (SiteCache hit)")>]
    member _.ComputedName() : obj = i <- i + 1; let n = names.[i % 2] in dlr { return (?) o n }

    [<Benchmark(Description = "dlr F# function member call w?Fn(1, 2)")>]
    member _.FunctionMember() : int = dlr { return o?Fn(1, 2) }

    [<Benchmark(Description = "dlr optional parameter omitted w?Bump(1)")>]
    member _.OptionalOmitted() : int = dlr { return o?Bump(1) }

    [<Benchmark(Description = "dlr F# lambda for a Func parameter w?Run(fun x -> x)")>]
    member _.FunctionToDelegate() : int = dlr { return o?Run(fun (x: int) -> x + 1) }

    [<Benchmark(Description = "dlr static overloads by runtime type")>]
    member _.StaticOverloads() : int = dlr { return Dlr.Static<Widget>.Overloads?Draw(o) }

    [<Benchmark(Description = "dlr Dlr.new'<Widget>()")>]
    member _.Construct() : Widget = dlr { return Dlr.new'<Widget>() }

    [<Benchmark(Description = "dlr structural record ?=?")>]
    member _.StructuralEquals() : bool =
        let a, b = box { X = 1; Y = 2 }, box { X = 1; Y = 2 }
        dlr { return a ?=? b }

[<ReflectedDefinition>]
[<MemoryDiagnoser>]
type Targets() =
    let json = box (JObject.Parse """{ "name": "j", "count": 3, "owner": { "name": "o" } }""")
    let expando =
        let e = System.Dynamic.ExpandoObject()
        (e :> Collections.Generic.IDictionary<string, obj>).["count"] <- box 3
        box e
    let jobject = json :?> JObject

    [<Benchmark(Baseline = true, Description = "JObject j.[\"count\"].Value<int>()")>]
    member _.JObjectStatic() = jobject.["count"].Value<int>()

    [<Benchmark(Description = "dlr JObject j?count")>]
    member _.JObjectGet() : int = dlr { return json?count }

    [<Benchmark(Description = "dlr JObject j?owner?name")>]
    member _.JObjectChain() : string = dlr { return json?owner?name }

    [<Benchmark(Description = "dlr Expando e?count")>]
    member _.ExpandoGet() : int = dlr { return expando?count }
