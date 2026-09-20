[<ReflectedDefinition>]
module Tests.Invoke

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

// The target is an `obj` from somewhere: a parameter, a plugin, a parsed document. The tests
// take it as a parameter so the blocks read as they would in a function that receives one.

[<Fact>]
let ``invoke with no args`` () =
    let describe (w: obj) : string = dlr { return w?Describe() }
    describe (Widget()) |> should equal "described"

[<Fact>]
let ``invoke with one arg uses the static type for overload resolution`` () =
    let w: obj = Widget()
    let n = 5
    let s = "five"
    let byInt: string = dlr { return w?Pick(n) }
    let byString: string = dlr { return w?Pick(s) }
    byInt |> should equal "int"
    byString |> should equal "string"

[<Fact>]
let ``invoke with an obj arg dispatches on the runtime type`` () =
    let w: obj = Widget()
    let o = box 5
    let picked: string = dlr { return w?Pick(o) }
    picked |> should equal "int"

[<Fact>]
let ``invoke with tuple args`` () =
    let add (w: obj) (a: int) (b: int) : int = dlr { return w?Add(a, b) }
    add (Widget()) 2 40 |> should equal 42

[<Fact>]
let ``invoke with literal args`` () =
    let w: obj = Widget()
    let sum: int = dlr { return w?Add(1, 2) }
    sum |> should equal 3

[<Fact>]
let ``named args reorder`` () =
    let w: obj = Widget()
    let greeting: string = dlr { return w?Greet(Dlr.named {| name = "Jay"; greeting = "Hi" |}) }
    greeting |> should equal "Hi, Jay"

[<Fact>]
let ``named args mix with positional`` () =
    let w: obj = Widget()
    let n = 10
    let stepped: int = dlr { return w?Bump(n, Dlr.named {| step = 5 |}) }
    let defaulted: int = dlr { return w?Bump(n) }
    stepped |> should equal 15
    defaulted |> should equal 11

[<Fact>]
let ``named args reach a DynamicObject by name`` () =
    let r = Recorder()
    let o: obj = r
    let s: string = dlr { return o?Call(1, Dlr.named {| second = 2 |}) }
    s |> should equal "1|2"
    List.ofSeq r.Log |> should equal [ "invoke Call(2 args; named second)" ]

[<Fact>]
let ``bare anonymous record is one positional arg`` () =
    let r = Recorder()
    let o: obj = r
    let _: string = dlr { return o?Call({| a = 1; b = 2 |}) }
    List.ofSeq r.Log |> should equal [ "invoke Call(1 args; named )" ]

[<Fact>]
let ``Dlr.named around a non-record is rejected`` () =
    let r: obj = Recorder()
    let x = 1
    let opts = {| second = 2 |}
    // The analyzer reports both at build time (DLR005); this pins the run-time error behind it.
    // fsharpanalyzer: ignore-line-next DLR005
    (fun () -> (dlr { return r?Call(Dlr.named x) } : string) |> ignore) |> should throw typeof<DlrTranslationException>
    // fsharpanalyzer: ignore-line-next DLR005
    (fun () -> (dlr { return r?Call(1, Dlr.named opts) } : string) |> ignore) |> should throw typeof<DlrTranslationException>

[<Fact>]
let ``unit result discards`` () =
    let w = Widget()
    let o: obj = w
    dlr { w?Touch() }
    dlr { do o?Touch() }
    w.Touched |> should equal 2

[<Fact>]
let ``invoke the target itself`` () =
    let f = box (Func<int, int>(fun x -> x * 2))
    let doubled: int = dlr { return f |> Dlr.apply 21 }
    doubled |> should equal 42

[<Fact>]
let ``delegate arg passes through`` () =
    let w: obj = Widget()
    let f = Func<int, int>(fun x -> x * 2)
    let ran: int = dlr { return w?Run(f) }
    ran |> should equal 42

[<Fact>]
let ``invoke the target with a unit result`` () =
    let mutable hits = 0
    let f = box (Action<int>(fun x -> hits <- hits + x))
    dlr { f |> Dlr.apply 5 }
    hits |> should equal 5

// Argument conversions C# applies to constants: boundary tables, one case per line.

[<Fact>]
let ``an int literal converts to a narrower parameter like a C# constant`` () =
    let w: obj = Widget()
    let n = 5
    (dlr { return w?Narrow(5) } : string) |> should equal "byte"
    (dlr { return w?Narrow(n) } : string) |> should equal "int64"

[<Fact>]
let ``the literal 0 converts to an enum parameter`` () =
    let w: obj = Widget()
    let z = 0
    (dlr { return w?Kind(0) } : string) |> should equal "enum"
    (dlr { return w?Kind(z) } : string) |> should equal "obj"

[<Fact>]
let ``a null literal picks the reference overload`` () =
    let w: obj = Widget()
    (dlr { return w?Text(null) } : string) |> should equal "string"

[<Fact>]
let ``the target is evaluated first, then the arguments left to right, each once`` () =
    // C#'s order, kept where the translator hoists something ahead of the call: a Dlr.named
    // record's temporaries (fields out of alphabetical order), a Dlr.namedOf / argsOf list, a
    // tuple, a computed name.
    let log = ResizeArray<string>()
    let t () = log.Add "target"; box (Recorder())
    let p () = log.Add "p"; 1
    let n (name: string) = log.Add name; 2
    let orderOf (run: unit -> unit) = log.Clear(); run (); List.ofSeq log
    orderOf (fun () -> (dlr { return (t ())?Call(p (), Dlr.named {| second = n "second"; first = n "first" |}) } : string) |> ignore)
    |> should equal [ "target"; "p"; "second"; "first" ]
    orderOf (fun () -> (dlr { return (t ())?Call(p (), Dlr.namedOf (log.Add "kw"; [ "second", box 2 ])) } : string) |> ignore)
    |> should equal [ "target"; "p"; "kw" ]
    orderOf (fun () -> (dlr { return (t ())?Call(Dlr.argsOf (log.Add "xs"; [ box 1 ]), p ()) } : string) |> ignore)
    |> should equal [ "target"; "xs"; "p" ]
    let w = Widget()
    let make () = log.Add "tuple"; (1, 2)
    orderOf (fun () -> (dlr { return (log.Add "target"; box w)?Add(make ()) } : int) |> ignore)
    |> should equal [ "target"; "tuple" ]
    let name () = log.Add "name"; "Call"
    orderOf (fun () -> (dlr { return (?) (t ()) (name ()) (p (), Dlr.named {| second = n "second"; first = n "first" |}) } : string) |> ignore)
    |> should equal [ "target"; "name"; "p"; "second"; "first" ]
    orderOf (fun () -> (dlr { return (t ()) |> Dlr.apply (p (), Dlr.namedOf (log.Add "kw"; [ "second", box 2 ])) } : string) |> ignore)
    |> should equal [ "target"; "p"; "kw" ]
    let s (name: string) = log.Add name; name
    orderOf (fun () -> (dlr { return Dlr.new'<Handler>(Dlr.named {| name = s "name"; count = n "count" |}) } : Handler) |> ignore)
    |> should equal [ "name"; "count" ]
    // A mutable read is not hoisted past an argument that assigns it.
    let mutable m = 1
    let r: obj = Recorder()
    let seen: string = dlr { return r?Call(m, Dlr.named {| second = (m <- 5; m); first = 0 |}) }
    seen |> should equal "1|0|5"

[<Fact>]
let ``a struct-typed target expression with a splat argument`` () =
    // F# eta-expands the call with the tuple's elements re-bound; the translator folds it back.
    let today () = DateTime(2020, 1, 1)
    let empty: obj list = []
    let next: DateTime = dlr { return (today ())?AddDays(1.0, Dlr.argsOf empty) }
    next |> should equal (DateTime(2020, 1, 2))
    let kw: (string * obj) list = []
    let same: DateTime = dlr { return (today ())?AddDays(1.0, Dlr.namedOf kw) }
    same |> should equal (DateTime(2020, 1, 2))

[<Fact>]
let ``a unit-valued argument expression is evaluated and passes no argument`` () =
    let w = Widget()
    let o: obj = w
    let log = ResizeArray<string>()
    let tick () = log.Add "tick"
    let described: string = dlr { return o?Describe(tick ()) }
    described |> should equal "described"
    dlr { o?Touch(tick ()) }
    w.Touched |> should equal 1
    List.ofSeq log |> should equal [ "tick"; "tick" ]

[<Fact>]
let ``a tuple in a variable is several arguments, as in F#'s own method calls`` () =
    let w: obj = Widget()
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
    let w: obj = Widget()
    let d = DayOfWeek.Monday
    (dlr { return w?Kind(box d) } : string) |> should equal "enum"
    (dlr { return w?Kind(d :> obj) } : string) |> should equal "enum"
    // Where static and runtime types differ: `Holders`-typed holding a `Derived`.
    let c: obj = Classifier()
    let b: Holders = Derived()
    (dlr { return c?Kind(b) } : string) |> should equal "holders"         // typed: bound by the static type
    (dlr { return c?Kind(box b) } : string) |> should equal "derived"     // obj: bound by the runtime type
    (dlr { return c?Kind(b :> obj) } : string) |> should equal "derived"  // the same as box (was "holders": the upcast was stripped)
    let s: obj = "text"
    (dlr { return w?Kind(s) } : string) |> should equal "obj"
    (dlr { return w?Kind(box 1) } : string) |> should equal "obj"
    (dlr { return w |> Dlr.invoke "Kind" (d :> obj) } : string) |> should equal "enum"
