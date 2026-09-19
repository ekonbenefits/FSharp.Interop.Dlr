[<ReflectedDefinition>]
module Tests.Invoke

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``invoke with no args`` () =
    let w = box (Widget())
    let s: string = dlr { return w?Describe() }
    s |> should equal "described"

[<Fact>]
let ``invoke with one arg uses the static type for overload resolution`` () =
    let w = box (Widget())
    let n = 5
    let s = "five"
    (dlr { return w?Pick(n) } : string) |> should equal "int"
    (dlr { return w?Pick(s) } : string) |> should equal "string"

[<Fact>]
let ``invoke with an obj arg dispatches on the runtime type`` () =
    let w = box (Widget())
    let o = box 5
    (dlr { return w?Pick(o) } : string) |> should equal "int"

[<Fact>]
let ``invoke with tuple args`` () =
    let w = box (Widget())
    let a, b = 2, 40
    (dlr { return w?Add(a, b) } : int) |> should equal 42

[<Fact>]
let ``invoke with literal args`` () =
    let w = box (Widget())
    (dlr { return w?Add(1, 2) } : int) |> should equal 3

[<Fact>]
let ``named args reorder`` () =
    let w = box (Widget())
    let s: string = dlr { return w?Greet(Dlr.named {| name = "Jay"; greeting = "Hi" |}) }
    s |> should equal "Hi, Jay"

[<Fact>]
let ``named args mix with positional`` () =
    let w = box (Widget())
    let n = 10
    (dlr { return w?Bump(n, Dlr.named {| step = 5 |}) } : int) |> should equal 15
    (dlr { return w?Bump(n) } : int) |> should equal 11

[<Fact>]
let ``named args reach a DynamicObject by name`` () =
    let r = Recorder()
    let o = box r
    let s: string = dlr { return o?Call(1, Dlr.named {| second = 2 |}) }
    s |> should equal "1|2"
    List.ofSeq r.Log |> should equal [ "invoke Call(2 args; named second)" ]

[<Fact>]
let ``bare anonymous record is one positional arg`` () =
    let r = Recorder()
    let o = box r
    let _: string = dlr { return o?Call({| a = 1; b = 2 |}) }
    List.ofSeq r.Log |> should equal [ "invoke Call(1 args; named )" ]

[<Fact>]
let ``Dlr.named around a non-record is rejected`` () =
    let r = box (Recorder())
    let x = 1
    (fun () -> (dlr { return r?Call(Dlr.named x) } : string) |> ignore)
    |> should throw typeof<DlrTranslationException>

[<Fact>]
let ``unit result discards`` () =
    let w = Widget()
    let o = box w
    dlr { w?Touch() }
    dlr { do o?Touch() }
    w.Touched |> should equal 2

[<Fact>]
let ``invoke the target itself`` () =
    let f = box (Func<int, int>(fun x -> x * 2))
    (dlr { return f |> Dlr.apply 21 } : int) |> should equal 42

[<Fact>]
let ``delegate arg passes through`` () =
    let w = box (Widget())
    let f = Func<int, int>(fun x -> x * 2)
    (dlr { return w?Run(f) } : int) |> should equal 42

[<Fact>]
let ``invoke the target with a unit result`` () =
    let mutable hits = 0
    let f = box (Action<int>(fun x -> hits <- hits + x))
    dlr { f |> Dlr.apply 5 }
    hits |> should equal 5

[<Fact>]
let ``explicit type argument when it cannot be inferred`` () =
    let w = box (Widget())
    (dlr { return w?TypeName(Dlr.typeArgs<int>()) } : string) |> should equal "Int32"
    (dlr { return w?Default(Dlr.typeArgs<int>()) } : int) |> should equal 0

[<Fact>]
let ``two explicit type arguments with positional args`` () =
    let w = box (Widget())
    (dlr { return w?Pair(Dlr.typeArgs<obj, string>(), 1, "x") } : string) |> should equal "Object/String"

[<Fact>]
let ``type argument inference still works without the marker`` () =
    let w = box (Widget())
    (dlr { return w?Echo(41) } : int) |> should equal 41

[<Fact>]
let ``wrong type argument arity raises RuntimeBinderException`` () =
    let w = box (Widget())
    (fun () -> (dlr { return w?TypeName(Dlr.typeArgs<int, int>()) } : string) |> ignore)
    |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``typeArgsOf takes a literal list of any length`` () =
    let w = box (Widget())
    (dlr { return w?TypeName(Dlr.typeArgsOf [ typeof<int> ]) } : string) |> should equal "Int32"
    (dlr { return w?Pair(Dlr.typeArgsOf [ typeof<obj>; typeof<string> ], 1, "x") } : string) |> should equal "Object/String"
    (dlr { return w?FiveNames(Dlr.typeArgsOf [ typeof<int>; typeof<string>; typeof<float>; typeof<bool>; typeof<char> ]) } : string)
    |> should equal "Int32/String/Double/Boolean/Char"
    // An empty list is no type arguments: inference as without the marker.
    (dlr { return w?Echo(Dlr.typeArgsOf [], 41) } : int) |> should equal 41

[<Fact>]
let ``typeArgsOf with a list only known at run time`` () =
    let w = box (Widget())
    let name (t: Type) : string = dlr { return w?TypeName(Dlr.typeArgsOf [ t ]) }
    name typeof<int> |> should equal "Int32"
    name typeof<string> |> should equal "String"
    name typeof<int> |> should equal "Int32"                        // one site, alternating lists
    let pair (a: Type) (b: Type) : string = dlr { return w?Pair(Dlr.typeArgsOf [ a; b ], 1, "x") }
    pair typeof<obj> typeof<string> |> should equal "Object/String"
    pair typeof<int> typeof<obj> |> should equal "Int32/Object"
    // Combined with a computed name: the key is both.
    let call (m: string) (t: Type) : string = dlr { return (?) w m (Dlr.typeArgsOf [ t ]) }
    call "TypeName" typeof<float> |> should equal "Double"
    call "TypeName" typeof<int> |> should equal "Int32"
    // An empty runtime list is inference; the wrong count is the binder's error.
    let echo (ts: Type list) : int = dlr { return w?Echo(Dlr.typeArgsOf ts, 41) }
    echo [] |> should equal 41
    let two = [ typeof<int>; typeof<int> ]
    (fun () -> (dlr { return w?TypeName(Dlr.typeArgsOf two) } : string) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``typeArgs must come first`` () =
    let w = box (Widget())
    (fun () -> (dlr { return w?Pair(1, Dlr.typeArgs<int, int>()) } : string) |> ignore)
    |> should throw typeof<DlrTranslationException>

[<Fact>]
let ``an int literal converts to a narrower parameter like a C# constant`` () =
    let w = box (Widget())
    let n = 5
    (dlr { return w?Narrow(5) } : string) |> should equal "byte"
    (dlr { return w?Narrow(n) } : string) |> should equal "int64"

[<Fact>]
let ``the literal 0 converts to an enum parameter`` () =
    let w = box (Widget())
    let z = 0
    (dlr { return w?Kind(0) } : string) |> should equal "enum"
    (dlr { return w?Kind(z) } : string) |> should equal "obj"

[<Fact>]
let ``a null literal picks the reference overload`` () =
    let w = box (Widget())
    (dlr { return w?Text(null) } : string) |> should equal "string"

[<Fact>]
let ``a tuple in a variable is several arguments, as in F#'s own method calls`` () =
    let w = box (Widget())
    let args = (40, 2)
    (dlr { return w?Add args } : int) |> should equal 42
    (dlr { return w |> Dlr.invoke "Add" args } : int) |> should equal 42
    let f = box (fun (a: int) (b: int) -> a + b)
    (dlr { return f |> Dlr.apply args } : int) |> should equal 42
    // Elements keep their static types: `Greet(string, string)` binds, not the obj overload.
    let pair = ("hello", "world")
    (dlr { return w?Greet pair } : string) |> should equal "hello, world"
    // Evaluated once.
    let mutable made = 0
    let make () = made <- made + 1; (1, 2)
    (dlr { return w?Add(make ()) } : int) |> should equal 3
    made |> should equal 1
    // A struct tuple is one value (as in F#), and `box t` passes a tuple as one dynamic argument.
    let st = struct (1, 2)
    (fun () -> (dlr { return w?Add st } : int) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> (dlr { return w?Add(box args) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``an argument upcast with :> obj dispatches on its runtime type, like box`` () =
    let w = box (Widget())
    let d = DayOfWeek.Monday
    (dlr { return w?Kind(box d) } : string) |> should equal "enum"
    (dlr { return w?Kind(d :> obj) } : string) |> should equal "enum"
    // Where static and runtime types differ: `Holders`-typed holding a `Derived`.
    let c = box (Classifier())
    let b: Holders = Derived()
    (dlr { return c?Kind(b) } : string) |> should equal "holders"         // typed: bound by the static type
    (dlr { return c?Kind(box b) } : string) |> should equal "derived"     // obj: bound by the runtime type
    (dlr { return c?Kind(b :> obj) } : string) |> should equal "derived"  // the same as box (was "holders": the upcast was stripped)
    let s: obj = "text"
    (dlr { return w?Kind(s) } : string) |> should equal "obj"
    (dlr { return w?Kind(box 1) } : string) |> should equal "obj"
    (dlr { return w |> Dlr.invoke "Kind" (d :> obj) } : string) |> should equal "enum"
