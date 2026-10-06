/// The library's hot paths against their alternatives. Every `dlr { }` here is bound and compiled
/// during BenchmarkDotNet's warm-up, so the numbers are the steady-state per-call cost: the block
/// delegate, the sites' cached rules, and whatever the operation itself does.
namespace FSharp.Interop.Dlr.Benchmarks

open System
open System.Reflection
open BenchmarkDotNet.Attributes
open FSharp.Interop.Dlr
open Newtonsoft.Json.Linq
open FSharp.Interop.Dlr.Benchmarks.CSharp

type Widget() =
    member val Count = 3 with get, set
    member val Name = "widget" with get, set
    member _.Add(a: int, b: int) = a + b
    member _.Sum6(a: int, b: int, c: int, d: int, e: int, f: int) = a + b + c + d + e + f
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
    let itemList = Collections.Generic.List<int>(items)
    let countProperty = typeof<Widget>.GetProperty("Count")
    let nameProperty = typeof<Widget>.GetProperty("Name")
    let addMethod = typeof<Widget>.GetMethod("Add")
    let sum6Method = typeof<Widget>.GetMethod("Sum6")
    let names = [| "Count"; "Name" |]
    let kwargs = [ "b", box 1; "a", box 2 ]
    let args = [ box 2; box 1 ]
    let kwargsOther = [ "a", box 2; "b", box 1 ]
    let adder2 = box (fun (a: int) (b: int) -> a + b)
    let one, two, three = box 1, box 2, box 3
    let dictionary = box (Collections.Generic.Dictionary<string, int>(dict [ "a", 1 ]))
    let adder = box (Func<int, int>(fun x -> x + 1))
    let mutable i = 0

    // --- baselines -----------------------------------------------------------------------
    [<Benchmark(Description = "static w.Count")>]
    member _.StaticGet() = w.Count

    // The baseline: the JIT folds the property read to nothing, a call it cannot.
    [<Benchmark(Baseline = true, Description = "static w.Add(i, 1)")>]
    member _.StaticCall() = i <- i + 1; w.Add(i, 1)

    [<Benchmark(Description = "static w.Sum6(1, 2, 3, 4, 5, i)")>]
    member _.StaticCallSum6() = i <- i + 1; w.Sum6(1, 2, 3, 4, 5, i)

    [<Benchmark(Description = "static w.Name <- v")>]
    member _.StaticSet() = w.Name <- "n"

    [<Benchmark(Description = "static loop of 100 calls (whole loop)")>]
    member _.StaticLoop() =
        let mutable s = 0
        for x in items do s <- s + w.Add(x, 1)
        s

    [<Benchmark(Description = "reflection: loop of 100 cached MethodInfo.Invoke (whole loop)")>]
    member _.ReflectionLoop() =
        let mutable s = 0
        for x in items do s <- s + (addMethod.Invoke(w, [| box x; box 1 |]) :?> int)
        s

    [<Benchmark(Description = "reflection: cached PropertyInfo.GetValue")>]
    member _.ReflectionGet() = countProperty.GetValue(w) :?> int

    [<Benchmark(Description = "reflection: cached MethodInfo.Invoke")>]
    member _.ReflectionCall() = i <- i + 1; addMethod.Invoke(w, [| box i; box 1 |]) :?> int

    [<Benchmark(Description = "reflection: cached MethodInfo.Invoke, six arguments")>]
    member _.ReflectionCallSum6() = sum6Method.Invoke(w, [| box 1; box 2; box 3; box 4; box 5; box i |]) :?> int

    [<Benchmark(Description = "reflection: cached PropertyInfo.SetValue")>]
    member _.ReflectionSet() = nameProperty.SetValue(w, "n")

    [<Benchmark(Description = "FSharp.Interop.Dynamic w?Add(Dyn.namedArg …)")>]
    member _.DynamicNamedArgs() = i <- i + 1; (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o "Add" (FSharp.Interop.Dynamic.Dyn.namedArg "b" (box 1), FSharp.Interop.Dynamic.Dyn.namedArg "a" (box i)) : int)

    [<Benchmark(Description = "FSharp.Interop.Dynamic w?Count")>]
    member _.DynamicGet() = (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o "Count" : int)

    [<Benchmark(Description = "FSharp.Interop.Dynamic w?Add(i, 1)")>]
    member _.DynamicCall() = i <- i + 1; (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o "Add" (i, 1) : int)

    [<Benchmark(Description = "FSharp.Interop.Dynamic w?Name <- v")>]
    member _.DynamicSet() = FSharp.Interop.Dynamic.TopLevelOperators.op_DynamicAssignment o "Name" "n"

    [<Benchmark(Description = "FSharp.Interop.Dynamic loop of 100 calls (whole loop)")>]
    member _.DynamicLoop() =
        let mutable s = 0
        for x in items do s <- s + (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o "Add" (x, 1) : int)
        s

    [<Benchmark(Description = "FSharp.Interop.Dynamic a ?+? b")>]
    member _.DynamicAdd() = (FSharp.Interop.Dynamic.Operators.op_QmarkPlusQmark one two : int)

    [<Benchmark(Description = "FSharp.Interop.Dynamic Dyn.getIndex")>]
    member _.DynamicIndex() = (FSharp.Interop.Dynamic.Dyn.getIndexer [ box "a" ] dictionary : int)

    [<Benchmark(Description = "FSharp.Interop.Dynamic !?delegate")>]
    member _.DynamicInvokeDelegate() = (FSharp.Interop.Dynamic.TopLevelOperators.op_BangQmark adder 20 : int)

    [<Benchmark(Description = "FSharp.Interop.Dynamic Dyn.implicitConvert")>]
    member _.DynamicConvert() = (FSharp.Interop.Dynamic.Dyn.implicitConvert three : int64)

    [<Benchmark(Description = "FSharp.Interop.Dynamic staticTarget Draw(o)")>]
    member _.DynamicStatic() = (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic (FSharp.Interop.Dynamic.Dyn.staticContext typeof<Widget>) "Draw" o : int)

    // --- C# dynamic (same binders, the C# compiler's own sites) --------------------------
    [<Benchmark(Description = "C# dynamic d.Count")>]
    member _.CSharpGet() = CSharpDynamic.Get o

    [<Benchmark(Description = "C# dynamic d.Add(i, 1)")>]
    member _.CSharpCall() = i <- i + 1; CSharpDynamic.Call(o, i)

    [<Benchmark(Description = "C# dynamic d.Name = v")>]
    member _.CSharpSet() = CSharpDynamic.Set o

    [<Benchmark(Description = "C# dynamic loop of 100 calls (whole loop)")>]
    member _.CSharpLoop() = CSharpDynamic.Loop(o, itemList)

    [<Benchmark(Description = "C# dynamic d.Run(new Func<int,int>(x => x + 1))")>]
    member _.CSharpRunFunc() = CSharpDynamic.RunFunc o

    [<Benchmark(Description = "C# dynamic Widget.Draw((dynamic)o)")>]
    member _.CSharpStaticOverloads() = CSharpDynamic.StaticOverloads o

    [<Benchmark(Description = "C# dynamic a + b")>]
    member _.CSharpAdd() = CSharpDynamic.Add(one, two)

    [<Benchmark(Description = "C# dynamic d[\"a\"]")>]
    member _.CSharpIndex() = CSharpDynamic.Index(dictionary, "a")

    [<Benchmark(Description = "C# dynamic d.Add(b: 1, a: i)")>]
    member _.CSharpNamedArgs() = i <- i + 1; CSharpDynamic.NamedArgs(o, i)

    [<Benchmark(Description = "C# dynamic d(20) on a delegate")>]
    member _.CSharpInvokeDelegate() = CSharpDynamic.InvokeDelegate(adder, 20)

    [<Benchmark(Description = "C# dynamic implicit conversion (long)d")>]
    member _.CSharpConvert() = CSharpDynamic.Convert three

    // --- FSharp.Interop.Dynamic, where the forms differ -----------------------------------
    [<Benchmark(Description = "FSharp.Interop.Dynamic record ?=? record")>]
    member _.DynamicEquals() =
        let a, b = box { X = 1; Y = 2 }, box { X = 1; Y = 2 }
        FSharp.Interop.Dynamic.Operators.op_QmarkEqualsQmark a b   // reference equality, as C#

    [<Benchmark(Description = "FSharp.Interop.Dynamic (?) o name, alternating")>]
    member _.DynamicComputedName() : obj = i <- i + 1; let n = names.[i % 2] in FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o n

    [<Benchmark(Description = "FSharp.Interop.Dynamic Dyn.namedArg from a list")>]
    member _.DynamicNamedOf() : int =
        let named = kwargs |> List.map (fun (n, v) -> FSharp.Interop.Dynamic.Dyn.namedArg n v)
        (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o "Add" (named.[0], named.[1]) : int)

    [<Benchmark(Description = "FSharp.Interop.Dynamic Dyn.namedArg, two lists alternating")>]
    member _.DynamicNamedOfAlternating() : int =
        i <- i + 1
        let named = (if i % 2 = 0 then kwargs else kwargsOther) |> List.map (fun (n, v) -> FSharp.Interop.Dynamic.Dyn.namedArg n v)
        (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o "Add" (named.[0], named.[1]) : int)

    [<Benchmark(Description = "FSharp.Interop.Dynamic w?Sum6(1, 2, 3, 4, 5, 6)")>]
    member _.DynamicCallSum6() : int = (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o "Sum6" (1, 2, 3, 4, 5, 6) : int)

    // Read at a tupled function type, its `?` is an invoker of the member; read curried, the first
    // application calls the method with one argument, which no overload takes (footnote 10).
    [<Benchmark(Description = "FSharp.Interop.Dynamic w?Add read as int * int -> int, then applied")>]
    member _.DynamicTupledRead2() : int =
        let f: int * int -> int = FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o "Add"
        f (1, 2)

    [<Benchmark(Description = "FSharp.Interop.Dynamic w?Sum6 read as a tupled function of six, then applied")>]
    member _.DynamicTupledRead6() : int =
        let f: int * int * int * int * int * int -> int = FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o "Sum6"
        f (1, 2, 3, 4, 5, 6)

    // FSharp.Interop.Dynamic applies a curried function one argument per application (`!?f 1 2`,
    // `w?Fn 1 2`); a tuple to a curried function is "not a delegate" to Dynamitey.
    [<Benchmark(Description = "FSharp.Interop.Dynamic !?f 1 2 on a curried F# function value")>]
    member _.DynamicInvokeFunction() : int = (FSharp.Interop.Dynamic.TopLevelOperators.op_BangQmark adder2 1 2 : int)

    [<Benchmark(Description = "FSharp.Interop.Dynamic w?Fn 1 2 on a curried F# function member")>]
    member _.DynamicFunctionMember() : int = (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic o "Fn" 1 2 : int)

    [<Benchmark(Description = "C# dynamic record == record")>]
    member _.CSharpEquals() =
        let a, b = box { X = 1; Y = 2 }, box { X = 1; Y = 2 }
        CSharpDynamic.AreEqual(a, b)   // C#'s reference equality on records (false here); dlr's is structural

    // --- dlr { } -------------------------------------------------------------------------
    [<Benchmark(Description = "C# dynamic d.TryGetValue(k, out int v)")>]
    member _.CSharpOutArg() = CSharpDynamic.OutArg dictionary

    [<Benchmark(Description = "dlr d?TryGetValue(k, Dlr.out)")>]
    member _.OutArg() : int =
        let (found: bool), (v: int) = dlr { return dictionary?TryGetValue("a", Dlr.out) }
        if found then v else 0

    [<Benchmark(Description = "dlr d?TryGetValue(k, Dlr.out) into a struct tuple")>]
    member _.OutArgStruct() : int =
        let struct (found: bool, v: int) = dlr { return dictionary?TryGetValue("a", Dlr.out) }
        if found then v else 0

    [<Benchmark(Description = "dlr w?Count")>]
    member _.Get() : int = dlr { return o?Count }

    [<Benchmark(Description = "dlr w?Add(i, 1)")>]
    member _.Call() : int = i <- i + 1; dlr { return o?Add(i, 1) }

    [<Benchmark(Description = "dlr w?Name <- v")>]
    member _.Set() = dlr { o?Name <- "n" }

    [<Benchmark(Description = "dlr loop of 100 calls, one site (whole loop)")>]
    member _.Loop() : int =
        dlr {
            let mutable s = 0
            for x in items do s <- s + (o?Add(x, 1) : int)
            return s
        }

    [<Benchmark(Description = "dlr named arguments w?Add(Dlr.named {| b = 1; a = i |})")>]
    member _.NamedArgs() : int = i <- i + 1; dlr { return o?Add(Dlr.named {| b = 1; a = i |}) }

    [<Benchmark(Description = "dlr keyword arguments from data w?Add(Dlr.namedOf kwargs)")>]
    member _.NamedOf() : int = dlr { return o?Add(Dlr.namedOf kwargs) }

    [<Benchmark(Description = "dlr positional arguments from data w?Add(Dlr.argsOf args)")>]
    member _.ArgsOf() : int = dlr { return o?Add(Dlr.argsOf args) }

    [<Benchmark(Description = "dlr Dlr.namedOf, two name lists alternating")>]
    member _.NamedOfAlternating() : int = i <- i + 1; let kw = (if i % 2 = 0 then kwargs else kwargsOther) in dlr { return o?Add(Dlr.namedOf kw) }

    [<Benchmark(Description = "dlr Dlr.call f read as int -> int -> int, then applied")>]
    member _.CallAsFunction() : int = let f: int -> int -> int = dlr { return Dlr.call adder2 } in f 1 2

    [<Benchmark(Description = "dlr w?Add read as int -> int -> int, then applied")>]
    member _.CurriedRead2() : int = let f: int -> int -> int = dlr { return o?Add } in f 1 2

    [<Benchmark(Description = "dlr w?Add read as int * int -> int, then applied")>]
    member _.TupledRead2() : int = let f: int * int -> int = dlr { return o?Add } in f (1, 2)

    [<Benchmark(Description = "dlr w?Add read as Func<int, int, int>, then invoked")>]
    member _.DelegateRead2() : int = let f: Func<int, int, int> = dlr { return o?Add } in f.Invoke(1, 2)

    [<Benchmark(Description = "dlr w?Sum6 read as a tupled function of six, then applied")>]
    member _.TupledRead6() : int = let f: int * int * int * int * int * int -> int = dlr { return o?Sum6 } in f (1, 2, 3, 4, 5, 6)

    [<Benchmark(Description = "dlr w?Sum6 read as a curried function of six, then applied")>]
    member _.CurriedRead6() : int = let f: int -> int -> int -> int -> int -> int -> int = dlr { return o?Sum6 } in f 1 2 3 4 5 6

    [<Benchmark(Description = "dlr w?Sum6(1, 2, 3, 4, 5, 6)")>]
    member _.CallSum6() : int = dlr { return o?Sum6(1, 2, 3, 4, 5, 6) }

    [<Benchmark(Description = "C# dynamic d.Sum6(1, 2, 3, 4, 5, 6)")>]
    member _.CSharpCallSum6() = CSharpDynamic.Sum6 o

    [<Benchmark(Description = "dlr (?) o name, name alternating (SiteCache hit)")>]
    member _.ComputedName() : obj = i <- i + 1; let n = names.[i % 2] in dlr { return (?) o n }

    [<Benchmark(Description = "dlr F# function member call w?Fn(1, 2)")>]
    member _.FunctionMember() : int = dlr { return o?Fn(1, 2) }

    [<Benchmark(Description = "dlr F# optional parameter omitted w?Bump(1)")>]
    member _.OptionalOmitted() : int = dlr { return o?Bump(1) }

    [<Benchmark(Description = "dlr F# lambda for a Func parameter w?Run(fun x -> x)")>]
    member _.FunctionToDelegate() : int = dlr { return o?Run(fun (x: int) -> x + 1) }

    [<Benchmark(Description = "dlr static overloads by runtime type")>]
    member _.StaticOverloads() : int = dlr { return Dlr.Static<Widget>.Overloads?Draw(o) }

    [<Benchmark(Description = "dlr Dlr.new'<Widget>()")>]
    member _.Construct() : Widget = dlr { return Dlr.new'<Widget>() }

    [<Benchmark(Description = "dlr a ?+? b")>]
    member _.Add() : int = dlr { return one ?+? two }

    [<Benchmark(Description = "dlr d |> Dlr.item \"a\"")>]
    member _.Index() : int = dlr { return dictionary |> Dlr.item "a" }

    [<Benchmark(Description = "dlr delegate |> Dlr.apply 20")>]
    member _.InvokeDelegate() : int = dlr { return adder |> Dlr.apply 20 }

    [<Benchmark(Description = "dlr Dlr.implicit to int64")>]
    member _.Convert() : int64 = dlr { return Dlr.implicit three }

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

    [<Benchmark(Description = "C# dynamic j.count")>]
    member _.CSharpJObjectGet() = CSharpDynamic.JObjectGet json

    [<Benchmark(Description = "C# dynamic j.owner.name")>]
    member _.CSharpJObjectChain() = CSharpDynamic.JObjectChain json

    [<Benchmark(Description = "C# dynamic e.count")>]
    member _.CSharpExpandoGet() = CSharpDynamic.ExpandoGet expando

    [<Benchmark(Description = "dlr JObject j?count")>]
    member _.JObjectGet() : int = dlr { return json?count }

    [<Benchmark(Description = "dlr JObject j?owner?name")>]
    member _.JObjectChain() : string = dlr { return json?owner?name }

    // Its `?` converts a result to `obj` before unboxing, which a JValue refuses (footnote 3), so
    // the read is `obj` and the conversion an explicit Dyn.implicitConvert.
    [<Benchmark(Description = "FSharp.Interop.Dynamic JObject j?count, then Dyn.implicitConvert")>]
    member _.DynamicJObjectGet() : int =
        FSharp.Interop.Dynamic.Dyn.implicitConvert (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic json "count" : obj)

    [<Benchmark(Description = "FSharp.Interop.Dynamic JObject j?owner?name, then Dyn.implicitConvert")>]
    member _.DynamicJObjectChain() : string =
        let owner : obj = FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic json "owner"
        FSharp.Interop.Dynamic.Dyn.implicitConvert (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic owner "name" : obj)

    [<Benchmark(Description = "FSharp.Interop.Dynamic e?count")>]
    member _.DynamicExpandoGet() = (FSharp.Interop.Dynamic.TopLevelOperators.op_Dynamic expando "count" : int)

    [<Benchmark(Description = "dlr Expando e?count")>]
    member _.ExpandoGet() : int = dlr { return expando?count }
