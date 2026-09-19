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
    (fun () -> (dlr { return r?Call(Dlr.named x) } : string) |> ignore)
    |> should throw typeof<DlrTranslationException>

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

[<Fact>]
let ``explicit type argument when it cannot be inferred`` () =
    let w: obj = Widget()
    let name: string = dlr { return w?TypeName(Dlr.typeArgs<int>()) }
    let zero: int = dlr { return w?Default(Dlr.typeArgs<int>()) }
    name |> should equal "Int32"
    zero |> should equal 0

[<Fact>]
let ``two explicit type arguments with positional args`` () =
    let w: obj = Widget()
    let pair: string = dlr { return w?Pair(Dlr.typeArgs<obj, string>(), 1, "x") }
    pair |> should equal "Object/String"

[<Fact>]
let ``type argument inference still works without the marker`` () =
    let w: obj = Widget()
    let echoed: int = dlr { return w?Echo(41) }
    echoed |> should equal 41

[<Fact>]
let ``wrong type argument arity raises RuntimeBinderException`` () =
    let w: obj = Widget()
    (fun () -> (dlr { return w?TypeName(Dlr.typeArgs<int, int>()) } : string) |> ignore)
    |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``typeArgsOf takes a literal list of any length`` () =
    let w: obj = Widget()
    let one: string = dlr { return w?TypeName(Dlr.typeArgsOf [ typeof<int> ]) }
    let two: string = dlr { return w?Pair(Dlr.typeArgsOf [ typeof<obj>; typeof<string> ], 1, "x") }
    let five: string = dlr { return w?FiveNames(Dlr.typeArgsOf [ typeof<int>; typeof<string>; typeof<float>; typeof<bool>; typeof<char> ]) }
    one |> should equal "Int32"
    two |> should equal "Object/String"
    five |> should equal "Int32/String/Double/Boolean/Char"
    // An empty list is no type arguments: inference as without the marker.
    let inferred: int = dlr { return w?Echo(Dlr.typeArgsOf [], 41) }
    inferred |> should equal 41

[<Fact>]
let ``typeArgsOf with a list only known at run time`` () =
    let w: obj = Widget()
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
    let names (ts: Type list) : string = dlr { return w?TypeName(Dlr.typeArgsOf ts) }
    (fun () -> names [ typeof<int>; typeof<int> ] |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``typeArgs must come first`` () =
    let w: obj = Widget()
    // The analyzer reports this at build time (DLR005); this pins the run-time error behind it.
    // fsharpanalyzer: ignore-line-next DLR005
    (fun () -> (dlr { return w?Pair(1, Dlr.typeArgs<int, int>()) } : string) |> ignore)
    |> should throw typeof<DlrTranslationException>

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

// Dlr.namedOf: named arguments whose names are run-time values (keyword arguments from data).
// The call is compiled once per distinct name list at the site and cached.

[<Fact>]
let ``namedOf: names from a list bind by name, in any order, mixed with positional and named`` () =
    let w = box (Widget())
    let greet (kw: (string * obj) list) : string = dlr { return w?Greet(Dlr.namedOf kw) }
    greet [ "greeting", box "Hi"; "name", box "Jay" ] |> should equal "Hi, Jay"
    greet [ "name", box "Jay"; "greeting", box "Yo" ] |> should equal "Yo, Jay"          // another order: its own delegate
    greet [ "greeting", box "Hi"; "name", box "Ann" ] |> should equal "Hi, Ann"          // the first again: cached
    let mixed (kw: (string * obj) list) : string = dlr { return w?Greet("Hey", Dlr.namedOf kw) }
    mixed [ "name", box "Jay" ] |> should equal "Hey, Jay"
    let both (kw: (string * obj) list) : string = dlr { return w?Greet(Dlr.named {| greeting = "Ho" |}, Dlr.namedOf kw) }
    both [ "name", box "Jay" ] |> should equal "Ho, Jay"
    (dlr { return w?Describe(Dlr.namedOf []) } : string) |> should equal "described"      // an empty list is no named arguments

[<Fact>]
let ``namedOf: two name lists alternate at one site, and many distinct lists stay correct past the bound`` () =
    let w = box (Widget())
    let add (kw: (string * obj) list) : int = dlr { return w?Add(Dlr.namedOf kw) }
    for i in 1 .. 50 do
        add [ "a", box i; "b", box 1 ] |> should equal (i + 1)
        add [ "b", box 1; "a", box i ] |> should equal (i + 1)
    // Past NamedOfCache.Capacity distinct lists (here: the same two names under different
    // *values* are one list; distinct lists need distinct names, so use Greet's optional-free
    // two names in the two orders plus a pile of misses that are the binder's error).
    let r = Recorder()
    let o = box r
    for i in 1 .. NamedOfCache.Capacity + 5 do
        let kw = [ sprintf "p%d" i, box i ]
        (dlr { return o?Call(Dlr.namedOf kw) } : string) |> should equal (string i)
    (dlr { return o?Call(Dlr.namedOf [ "p1", box 1 ]) } : string) |> should equal "1"     // still fine after the clear

[<Fact>]
let ``namedOf: a name matching no parameter is the binder's error`` () =
    let w = box (Widget())
    (fun () -> (dlr { return w?Add(Dlr.namedOf [ "nope", box 1; "b", box 2 ]) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``namedOf: a constructor, a static overload set, Dlr.apply on a TryInvoke object`` () =
    let h: Handler = dlr { return Dlr.new'<Handler>(Dlr.namedOf [ "count", box 3; "name", box "n" ]) }
    h.Kind |> should equal "named"
    h.Detail |> should equal "n:3"
    (dlr { return Dlr.Static<Statics>.Overloads?BumpF(Dlr.namedOf [ "count", box 1; "step", box 10 ]) } : int) |> should equal 11
    let r = Recorder()
    let o = box r
    (dlr { return o |> Dlr.apply (1, Dlr.namedOf [ "second", box 2 ]) } : string) |> should equal "1|2"
    List.ofSeq r.Log |> should equal [ "invoke self(2 args)" ]

[<Fact>]
let ``namedOf: with a computed member name, and with run-time type arguments`` () =
    // Both from data: the function named in config with the keyword arguments from config.
    let w = box (Widget())
    let call (name: string) (kw: (string * obj) list) : string = dlr { return (?) w name (Dlr.namedOf kw) }
    call "Greet" [ "name", box "Jay"; "greeting", box "Hi" ] |> should equal "Hi, Jay"
    call "Greet" [ "greeting", box "Yo"; "name", box "Ann" ] |> should equal "Yo, Ann"       // another name list, same member
    let add (name: string) (kw: (string * obj) list) : int = dlr { return (?) w name (Dlr.namedOf kw) }
    add "Add" [ "b", box 2; "a", box 1 ] |> should equal 3
    for i in 1 .. 20 do
        call "Greet" [ "name", box (string i); "greeting", box "N" ] |> should equal ("N, " + string i)
        add "Add" [ "a", box i; "b", box i ] |> should equal (i + i)
    let ts = [ typeof<int64> ]
    (dlr { return w?Echo(Dlr.typeArgsOf ts, Dlr.namedOf [ "x", box 42L ]) } : int64) |> should equal 42L

[<Fact>]
let ``an argument marker anywhere but in a call's arguments is a translation error, not an executed marker`` () =
    let w = box (Widget())
    let kw = [ "a", box 1 ]
    // fsharpanalyzer: ignore-region-start DLR005
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> (dlr { return box (Dlr.namedOf kw) } : obj) |> ignore)
    ex.Message |> should haveSubstring "Dlr.namedOf anywhere but as an argument"
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> (dlr { return w |> Dlr.item (Dlr.named {| p = 2 |}) } : int) |> ignore)
    ex.Message |> should haveSubstring "Dlr.named anywhere but as an argument"
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> (dlr { let args = (1, Dlr.named {| p = 2 |}) in return w?Add args } : int) |> ignore)
    ex.Message |> should haveSubstring "Dlr.named anywhere but as an argument"
    // fsharpanalyzer: ignore-region-end DLR005

// Dlr.argsOf: positional arguments whose count is a run-time value (Python's *args), through
// the same per-shape compile as Dlr.namedOf: an empty name in the key stands for a positional.

[<Fact>]
let ``argsOf: a list of 0, 1, 3 values; mixed with fixed, Dlr.named and Dlr.namedOf in source order`` () =
    let w = box (Widget())
    let sum (xs: obj list) : int = dlr { return w?Sum6(Dlr.argsOf xs) }
    (fun () -> sum [] |> ignore) |> should throw typeof<RuntimeBinderException>                  // Sum6 takes six
    let add (xs: obj list) : int = dlr { return w?Add(Dlr.argsOf xs) }
    add [ box 1; box 2 ] |> should equal 3
    add [ box 3; box 4 ] |> should equal 7                                                      // same count: cached
    let one (xs: obj list) : int = dlr { return w?Add(Dlr.argsOf xs, 10) }                     // fixed after the splat
    one [ box 1 ] |> should equal 11
    let three (xs: obj list) : string = dlr { return w?Greet(Dlr.argsOf xs) }
    (fun () -> three [ box "a"; box "b"; box "c" ] |> ignore) |> should throw typeof<RuntimeBinderException>
    let mixed (xs: obj list) (kw: (string * obj) list) : string = dlr { return w?Greet("Hi", Dlr.argsOf xs, Dlr.namedOf kw) }
    mixed [] [ "name", box "Jay" ] |> should equal "Hi, Jay"
    mixed [ box "Jay" ] [] |> should equal "Hi, Jay"
    let both (xs: obj list) (kw: (string * obj) list) : string = dlr { return w?Greet(Dlr.argsOf xs, Dlr.named {| name = "Ann" |}, Dlr.namedOf kw) }
    both [ box "Yo" ] [] |> should equal "Yo, Ann"
    (dlr { return w?Describe(Dlr.argsOf []) } : string) |> should equal "described"

[<Fact>]
let ``argsOf: two counts alternate at one site; Dlr.apply, a constructor, a static overload set; errors`` () =
    let w = box (Widget())
    let sum (xs: obj list) : int = dlr { return w?Sum6(Dlr.argsOf xs) }
    let add (xs: obj list) : int = dlr { return w?Add(Dlr.argsOf xs) }
    for i in 1 .. 30 do
        add [ box i; box 1 ] |> should equal (i + 1)
        sum [ box i; box 1; box 1; box 1; box 1; box 1 ] |> should equal (i + 5)
    let r = Recorder()
    let o = box r
    (dlr { return o |> Dlr.apply (Dlr.argsOf [ box 1; box 2 ]) } : string) |> should equal "1|2"
    let h: Handler = dlr { return Dlr.new'<Handler>(Dlr.argsOf [ box "n"; box 3 ]) }
    h.Detail |> should equal "n:3"
    (dlr { return Dlr.Static<Statics>.Overloads?BumpF(Dlr.argsOf [ box 1; box 10 ]) } : int) |> should equal 11
    // Errors: twice; a positional after Dlr.namedOf.
    // fsharpanalyzer: ignore-region-start DLR005
    let xs = [ box 1 ]
    (fun () -> (dlr { return w?Add(Dlr.argsOf xs, Dlr.argsOf xs) } : int) |> ignore) |> should throw typeof<DlrTranslationException>
    (fun () -> (dlr { return w?Add(Dlr.namedOf [ "b", box 2 ], Dlr.argsOf xs) } : int) |> ignore) |> should throw typeof<DlrTranslationException>
    // fsharpanalyzer: ignore-region-end DLR005
