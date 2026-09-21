/// Arguments whose names or count are run-time values: `Dlr.namedOf` (Python `**kwargs`) and `Dlr.argsOf` (`*args`).
[<ReflectedDefinition>]
module Tests.ArgsFromData

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

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

[<Fact>]
let ``a null or empty name from data is an argument error, not a positional slot`` () =
    let w: obj = Widget()
    let add (kw: (string * obj) list) : int = dlr { return w?Add(Dlr.namedOf kw) }
    (fun () -> add [ "", box 1; "b", box 2 ] |> ignore) |> should throw typeof<ArgumentException>
    (fun () -> add [ "a", box 1; null, box 2 ] |> ignore) |> should throw typeof<ArgumentNullException>
    (fun () -> add (Unchecked.defaultof<_>) |> ignore) |> should throw typeof<ArgumentNullException>
    // The empty name does not match the positional slots of an argsOf shape already compiled at the site.
    let mixed (xs: obj list) (kw: (string * obj) list) : int = dlr { return w?Add(Dlr.argsOf xs, Dlr.namedOf kw) }
    mixed [ box 1; box 2 ] [] |> should equal 3
    (fun () -> mixed [] [ "", box 1; "", box 2 ] |> ignore) |> should throw typeof<ArgumentException>
    let name (ts: Type list) : string = dlr { return w?TypeName(Dlr.typeArgsOf ts) }
    (fun () -> name [ null ] |> ignore) |> should throw typeof<ArgumentException>
    (fun () -> name (Unchecked.defaultof<_>) |> ignore) |> should throw typeof<ArgumentNullException>
    let read (n: string) : obj = dlr { return (?) w n }
    (fun () -> read null |> ignore) |> should throw typeof<ArgumentNullException>
    // The site is unharmed.
    add [ "a", box 1; "b", box 2 ] |> should equal 3
    name [ typeof<int> ] |> should equal "Int32"
    read "Count" |> should equal (box 3)


[<Fact>]
let ``argsOf takes at most 64 values: a longer collection is one argument`` () =
    let w: obj = Widget()
    let sum (xs: obj list) : int = dlr { return w?SumAll(Dlr.argsOf xs) }
    let ex = AnyUnit.Run.Assert.Current.Throws<ArgumentException>(fun () -> sum [ for i in 1 .. 65 -> box i ] |> ignore)
    ex.Message |> should haveSubstring "at most 64"
    sum [ for i in 1 .. 64 -> box i ] |> should equal (64 * 65 / 2)
    sum [ for i in 1 .. 15 -> box i ] |> should equal 120                 // the first wide arity: 15 arguments

let private fifteen = [ for i in 1 .. 15 -> box i ]

[<Fact>]
let ``wide site: written out, and a void one`` () =
    let w = Widget()
    let o: obj = w
    (dlr { return o?SumAll(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15) } : int) |> should equal 120
    dlr { o?Touch15(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15) }
    w.Touched |> should equal 120

[<Fact>]
let ``wide site: a computed name`` () =
    let w: obj = Widget()
    let named (name: string) (xs: obj list) : int = dlr { return (?) w name (Dlr.argsOf xs) }
    named "SumAll" fifteen |> should equal 120

[<Fact>]
let ``wide site: Dlr.invoke, a static overload set, Dlr.apply`` () =
    let w: obj = Widget()
    (dlr { return w |> Dlr.invoke "SumAll" (Dlr.argsOf fifteen) } : int) |> should equal 120
    (dlr { return Dlr.Static<Statics>.Overloads?SumAll(Dlr.argsOf fifteen) } : int) |> should equal 120
    let f = box (fun (a: int) (b: int) (c: int) (d: int) (e: int) (f: int) (g: int) (h: int) (i: int) (j: int) (k: int) (l: int) (m: int) (n: int) (o: int) -> a + b + c + d + e + f + g + h + i + j + k + l + m + n + o)
    (dlr { return f |> Dlr.apply (Dlr.argsOf fifteen) } : int) |> should equal 120

[<Fact>]
let ``wide site: fourteen written out plus a splat, plain and with a computed name`` () =
    let w: obj = Widget()
    let rest = [ box 15 ]
    (dlr { return w?SumAll(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, Dlr.argsOf rest) } : int) |> should equal 120
    let named (name: string) (xs: obj list) : int = dlr { return (?) w name (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, Dlr.argsOf xs) }
    named "SumAll" rest |> should equal 120
    // The array itself is one argument to the params parameter, however long.
    let all: int = dlr { return w?SumAll([| 1 .. 1000 |]) }
    all |> should equal (1000 * 1001 / 2)
