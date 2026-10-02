/// Members holding F# function values (not delegates), which the C# binder alone cannot invoke.
[<ReflectedDefinition>]
module Tests.FunctionMembers

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

type Handlers = { OnValue: int -> int; OnPair: int -> int -> int }

let private bag () =
    Fixtures.expando [
        "Del", box (Func<int, int>(fun x -> x * 2))
        "Fn", box (fun (x: int) -> x * 2)
        "Curried", box (fun (a: int) (b: int) -> a + b)
        "Tupled", box (fun (a: int, b: int) -> a * b)
        "Thunk", box (fun () -> "ran")
        "Three", box (fun (a: int) (b: int) (c: int) -> a + b + c)
        "Four", box (fun (a: int) (b: int) (c: int) (d: int) -> a * 1000 + b * 100 + c * 10 + d)
        "FourTupled", box (fun (a: int, b: int, c: int, d: int) -> a + b + c + d)
        "Do", box (fun () -> ())
    ]

[<Fact>]
let ``an F# function in a dynamic member is invoked like a delegate would be`` () =
    let e: obj = bag ()
    let viaDelegate: int = dlr { return e?Del(21) }            // delegate: the binder's own path
    let viaFunction: int = dlr { return e?Fn(21) }             // FSharpFunc: the fallback
    let viaThunk: string = dlr { return e?Thunk() }
    viaDelegate |> should equal 42
    viaFunction |> should equal 42
    viaThunk |> should equal "ran"

[<Fact>]
let ``curried and tupled functions both take a tuple call`` () =
    let e: obj = bag ()
    let curried: int = dlr { return e?Curried(1, 2) }
    let tupled: int = dlr { return e?Tupled(3, 4) }
    let three: int = dlr { return e?Three(1, 2, 3) }
    curried |> should equal 3
    tupled |> should equal 12
    three |> should equal 6

[<Fact>]
let ``four arguments, curried and tupled, as a member call and through Dlr.apply`` () =
    let e: obj = bag ()
    let curried: int = dlr { return e?Four(1, 2, 3, 4) }
    let tupled: int = dlr { return e?FourTupled(1, 2, 3, 4) }
    curried |> should equal 1234
    tupled |> should equal 10
    let applied: int = dlr { return (e |> Dlr.get "Four") |> Dlr.apply (4, 3, 2, 1) }
    let appliedTupled: int = dlr { return (e |> Dlr.get "FourTupled") |> Dlr.apply (1, 1, 1, 1) }
    applied |> should equal 4321
    appliedTupled |> should equal 4
    let f: int -> int -> int -> int -> int = dlr { return e?Four }
    f 5 6 7 8 |> should equal 5678

[<Fact>]
let ``a unit-returning F# function member called as a statement`` () =
    let hits = ResizeArray<string>()
    let e = box (Fixtures.expando [ "Log", box (fun (s: string) -> hits.Add s); "Tick", box (fun () -> hits.Add "tick") ])
    dlr { e?Log("a") }
    dlr { e?Tick() }
    let tick = box (fun () -> hits.Add "called")
    dlr { tick |> Dlr.apply () }
    let w = Widget()
    let o: obj = w
    dlr { o?Touch() }                        // a void method still binds with the result discarded
    List.ofSeq hits |> should equal [ "a"; "tick"; "called" ]
    w.Touched |> should equal 1

[<Fact>]
let ``a curried function applied F# style goes through the optimized closure`` () =
    let e: obj = bag ()
    let r: int = dlr { return (e?Curried : int -> int -> int) 10 5 }
    r |> should equal 15

[<Fact>]
let ``a member read as a function type is a curried invoker of it`` () =
    // Whatever the member is: an F# function, a delegate, a method. Applied when fully applied.
    let e: obj = bag ()
    let f: int -> int = dlr { return e?Fn }
    f 4 |> should equal 8
    let g: int -> int -> int = dlr { return e |> Dlr.get "Curried" }
    g 2 3 |> should equal 5
    let d: int -> int = dlr { return e?Del }
    d 21 |> should equal 42
    let w: obj = Widget()
    let add: int -> int -> int = dlr { return w?Add }
    add 40 2 |> should equal 42
    let addTupled: int * int -> int = dlr { return w?Add }
    addTupled (40, 2) |> should equal 42

[<Fact>]
let ``a function-typed result applied on the spot, with nothing captured`` () =
    // The compiler takes the non-resumable path for this shape without a warning and, the block
    // capturing nothing, leaves a static delegate: the library compiles from its closure class.
    (dlr { return Fixtures.plainWidget?Count } : unit -> int) () |> should equal 3
    (dlr { return Fixtures.plainWidget?Add } : int -> int -> int) 40 2 |> should equal 42
    (dlr { return (Widget() :> obj)?Describe } : unit -> string) () |> should equal "described"
    let mk () = box (Widget())                                           // a local function is not a captured value either
    (dlr { return (mk ())?Count } : unit -> int) () |> should equal 3
    let w: obj = Widget()                                                // a captured value: the closure itself
    (dlr { return w?Count } : unit -> int) () |> should equal 3

[<Fact>]
let ``a curried invoker supports partial application and converts its result`` () =
    let w: obj = Widget()
    let add: int -> int -> int64 = dlr { return w?Add }
    let add40 = add 40
    add40 2 |> should equal 42L
    let pick: obj -> string = dlr { return w?Pick }
    pick (box 1) |> should equal "int"

[<Fact>]
let ``unit -> R reads a property or invokes a parameterless method`` () =
    let w = Widget()
    let o: obj = w
    let count: unit -> int = dlr { return o?Count }
    count () |> should equal 3
    w.Count <- 5
    count () |> should equal 5                       // deferred: reads on each call
    let describe: unit -> string = dlr { return o?Describe }
    describe () |> should equal "described"

[<Fact>]
let ``a computed name read as a function type`` () =
    let w: obj = Widget()
    let bind (name: string) : int -> int -> int = dlr { return (?) w name }
    (bind "Add") 1 2 |> should equal 3

[<Fact>]
let ``Dlr.apply applies an F# function target`` () =
    let e: obj = bag ()
    let one: int = dlr { return (e |> Dlr.get "Fn") |> Dlr.apply 21 }
    let two: int = dlr { return (e |> Dlr.get "Curried") |> Dlr.apply (4, 5) }
    one |> should equal 42
    two |> should equal 9
    let thunk = box (fun () -> 7)
    let none: int = dlr { return thunk |> Dlr.apply () }
    none |> should equal 7

[<Fact>]
let ``record fields holding functions on a CLR type`` () =
    let h = box { OnValue = (fun x -> x + 1); OnPair = (fun a b -> a * b) }
    (dlr { return h?OnValue(1) } : int) |> should equal 2
    (dlr { return h?OnPair(3, 4) } : int) |> should equal 12

[<Fact>]
let ``a wrong argument type still reports the binder's own error`` () =
    let e: obj = bag ()
    let s = "not an int"
    (fun () -> (dlr { return e?Fn(s) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> (dlr { return e?Missing(1) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``computed names take the same fallback`` () =
    let e: obj = bag ()
    let call (name: string) : int = dlr { return ((?) e name) (21) }
    call "Del" |> should equal 42
    call "Fn" |> should equal 42

[<Fact>]
let ``a discarded result still applies the function that is actually there`` () =
    // The statement form infers `int -> unit`; the member is `int -> int`. The shape comes from the
    // function, not the call, so it is applied and its value dropped, as C# drops a discarded result.
    let calls = ResizeArray<int>()
    let e = box (Fixtures.expando [ "Fn", box (fun (x: int) -> calls.Add x; x * 2) ])
    dlr { e?Fn(21) }
    let f = box (fun (x: int) -> calls.Add x; x)
    dlr { f |> Dlr.apply 7 }
    List.ofSeq calls |> should equal [ 21; 7 ]

[<Fact>]
let ``a CLR member declared obj holding a function is applied by its runtime type`` () =
    let h = box (Holders())
    (dlr { return h?AsObj(2) } : int) |> should equal 6
    (dlr { return h?AsFunction(2) } : int) |> should equal 3

[<Fact>]
let ``a CLR delegate property read as unit -> R is invoked, not returned`` () =
    let h = box (Holders())
    let d: unit -> int = dlr { return h?AsDelegate }
    d () |> should equal 9
    let f: int -> int = dlr { return h?AsObj }
    f 5 |> should equal 15

[<Fact>]
let ``an indexed property is left to C#, which binds it as an index, not a member call`` () =
    let h = box (Holders())
    (dlr { return h |> Dlr.item 2 } : int) |> should equal 20
    // As in C#, `d.Item(4)` is not how an indexer is called; the error is the binder's, not a
    // bind-time failure building a property read without its index.
    (fun () -> (dlr { return h?Item(4) } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``non-public F# function members and optional-parameter methods bind from an allowed context`` () =
    // F# `member private` is IL internal: the whole assembly is an allowed context, like C#'s binder.
    let h = Holders()
    h.Reveal(h) |> should equal 9
    h.RevealOptional(h) |> should equal 101
    (dlr { return (box h)?Hidden(3) } : int) |> should equal 2

[<Fact>]
let ``an obj-typed argument matches a function shape by its runtime type`` () =
    let e = box (Fixtures.expando [ "Fn", box (fun (x: int) -> x * 2); "Two", box (fun (a: int) (b: string) -> sprintf "%d%s" a b) ])
    let n = box 21
    let a, b = box 4, box "x"
    (dlr { return e?Fn(n) } : int) |> should equal 42
    (dlr { return e?Two(a, b) } : string) |> should equal "4x"
    let f = box (fun (x: int) -> x + 1)
    (dlr { return f |> Dlr.apply n } : int) |> should equal 22

[<Fact>]
let ``unit -> unit binds a void method, an Action and a unit function`` () =
    let w = Widget()
    let touch: unit -> unit = dlr { return (box w)?Touch }
    touch ()
    let hits = ref 0
    let e = box (Fixtures.expando [ "Act", box (Action(fun () -> hits.Value <- hits.Value + 1)); "Fn", box (fun () -> hits.Value <- hits.Value + 10) ])
    let act: unit -> unit = dlr { return e?Act }
    let fn: unit -> unit = dlr { return e?Fn }
    act ()
    fn ()
    let log: string -> unit = dlr { return e?Fn2 }   // bound before the member exists...
    (fun () -> log "x") |> should throw typeof<RuntimeBinderException>
    (w.Touched, hits.Value) |> should equal (1, 11)

[<Fact>]
let ``an int argument fits an int64 function domain by C#'s implicit widening`` () =
    let h = box (Holders())
    (dlr { return h?Wide(41) } : int64) |> should equal 42L
    let e = box (Fixtures.expando [ "F", box (fun (x: float) -> x * 2.0) ])
    (dlr { return e?F(21) } : float) |> should equal 42.0
    // A boxed int (dynamic argument) widens the same way: unboxed as int, then converted.
    let n = box 41
    (dlr { return h?Wide(n) } : int64) |> should equal 42L
    (dlr { return e?F(n) } : float) |> should equal 82.0
    let m = box 2
    (dlr { return h?WideTupled(n, m) } : int64) |> should equal 43L
    (dlr { return h?WideCurried(n, m) } : int64) |> should equal 43L

[<Fact>]
let ``optional-parameter overloads pick the more specific one deterministically`` () =
    let h = box (Holders())
    let s = "x"
    (dlr { return h?Overloaded(s) } : string) |> should equal "string:x"
    (dlr { return h?Overloaded(box 1) } : string) |> should equal "obj:1"

[<Fact>]
let ``a protected member binds from a derived context`` () =
    let d = Derived()
    d.CallFamily(d) |> should equal 15

/// Derives from a generic outer whose protected nested type declares the members.
type DerivedG() =
    inherit Tests.CSharp.GOuter<int>()
    // F# lets a protected call be made only outside the closure the block compiles to.
    member this.ReadNested() : int = let nested = this.MakeNested() in (dlr { return nested?Value } : unit -> int) ()
    member this.CallNested() : int = let nested = this.MakeNested() in dlr { return nested?Fn(1) }

/// Derives from Outer<int>: protected members bind through a receiver of this type only.
type DerivedOfInt() =
    inherit Tests.CSharp.Outer<int>()
    member this.ReadP(o: obj) : int = (dlr { return o?P } : unit -> int) ()
    member this.CallQ(o: obj) : int = dlr { return o?Q(1) }
    member this.Self = box this

[<Fact>]
let ``a protected instance member binds only through a receiver of the derived context's type, as in C#`` () =
    let d = DerivedOfInt()
    d.ReadP d.Self |> should equal 5
    d.CallQ d.Self |> should equal 6
    // The base itself, another instantiation of it, a sibling derived type: C#'s qualifier rule.
    for receiver in [ Tests.CSharp.Make.OuterOfInt(); Tests.CSharp.Make.OuterOfString(); box (Tests.CSharp.SiblingOfInt()) ] do
        (fun () -> d.ReadP receiver |> ignore) |> should throw typeof<RuntimeBinderException>
        (fun () -> d.CallQ receiver |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``a protected type nested in a generic outer is visible from a derived context`` () =
    let d = DerivedG()
    d.ReadNested() |> should equal 7
    d.CallNested() |> should equal 2

[<Fact>]
let ``calling an F# function member has no arity limit`` () =
    let h = box (Holders())
    (dlr { return h?Five(1, 2, 3, 4, 5) } : int) |> should equal 15
    (dlr { return h?Six(1, 2, 3, 4, 5, 6) } : int) |> should equal 720
    (dlr { return h?SixTupled(1, 2, 3, 4, 5, 6) } : int) |> should equal 21
    (dlr { return h?Eight(1, 2, 3, 4, 5, 6, 7, 8) } : int) |> should equal 36
    (dlr { return h?EightTupled(1, 2, 3, 4, 5, 6, 7, 8) } : int) |> should equal 36   // past seven the CLR tuple nests
    (dlr { return h?EightStruct(1, 2, 3, 4, 5, 6, 7, 8) } : int) |> should equal 36
    (dlr { return h?SixteenTupled(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16) } : int) |> should equal 136   // and the rest tuple nests again
    let six = box (fun (a: int) (b: int) (c: int) (d: int) (e: int) (f: int) -> a + b + c + d + e + f)
    (dlr { return six |> Dlr.apply (1, 1, 1, 1, 1, 1) } : int) |> should equal 6

[<Fact>]
let ``reading a member as a curried function has no arity limit`` () =
    let h = box (Holders())
    let five: int -> int -> int -> int -> int -> int = dlr { return h?Five }
    five 1 2 3 4 5 |> should equal 15
    // Past five, a run-time-built curried closure, as F# itself does past OptimizedClosures.
    let six: int -> int -> int -> int -> int -> int -> int = dlr { return h?Six }
    six 1 2 3 4 5 6 |> should equal 720
    let partial = six 1 2 3
    partial 4 5 6 |> should equal 720
    partial 1 1 1 |> should equal 6        // each step keeps its own arguments: a partial application is reusable
    let eight: int -> int -> int -> int -> int -> int -> int -> int -> int64 = dlr { return h?Eight }
    eight 1 2 3 4 5 6 7 8 |> should equal 36L
    // A C# method of six parameters, bound curried.
    let sum6: int -> int -> int -> int -> int -> int -> int = dlr { return (box (Widget()))?Sum6 }
    sum6 1 2 3 4 5 6 |> should equal 21
    // A unit result past five: the site is void, the last step returns unit.
    let sixUnit: int -> int -> int -> int -> int -> int -> unit = dlr { return h?Six }
    sixUnit 1 2 3 4 5 6

[<Fact>]
let ``reading a member or value as a tupled function has no arity limit`` () =
    // Past five, a tupled function built at run time: one argument, the whole tuple.
    let h = box (Holders())
    let five: int * int * int * int * int -> int = dlr { return h?Five }
    five (1, 2, 3, 4, 5) |> should equal 15
    let six: int * int * int * int * int * int -> int = dlr { return h?SixTupled }
    six (1, 2, 3, 4, 5, 6) |> should equal 21
    let sum6: int * int * int * int * int * int -> int = dlr { return (box (Widget()))?Sum6 }       // a C# method of six parameters
    sum6 (1, 2, 3, 4, 5, 6) |> should equal 21
    let eight: int * int * int * int * int * int * int * int -> int = dlr { return h?EightTupled }   // past seven: the CLR tuple nests
    eight (1, 2, 3, 4, 5, 6, 7, 8) |> should equal 36
    let eightStruct: struct (int * int * int * int * int * int * int * int) -> int = dlr { return h?EightStruct }
    eightStruct (struct (1, 2, 3, 4, 5, 6, 7, 8)) |> should equal 36
    // Sixteen: the site is past Func's arity, so its delegate type is emitted at run time — which
    // a quotation must not name (wasm). The site goes in as a CallSite constant, coerced to the base.
    let sixteen: int * int * int * int * int * int * int * int * int * int * int * int * int * int * int * int -> int = dlr { return h?SixteenTupled }
    sixteen (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16) |> should equal 136
    let sixUnit: int * int * int * int * int * int -> unit = dlr { return h?SixTupled }               // a unit result: a void site
    sixUnit (1, 2, 3, 4, 5, 6)
    let name = "SixTupled"
    let keyed: int * int * int * int * int * int -> int = dlr { return (?) h name }                   // a computed name: a per-key site
    keyed (1, 1, 1, 1, 1, 1) |> should equal 6
    let f = box (Func<int, int, int, int, int, int, int>(fun a b c d e g -> a + b + c + d + e + g))
    let value: int * int * int * int * int * int -> int = dlr { return Dlr.call f }                   // a value read as a function
    value (1, 2, 3, 4, 5, 6) |> should equal 21

[<Fact>]
let ``a method of only optional parameters can be read as unit -> R`` () =
    let o: obj = Widget()
    let wrap: unit -> string = dlr { return o?Wrap }
    wrap () |> should equal "<x>"

/// A null argument to an F# function member, or to `Dlr.apply` on a function: the rule's
/// restriction on that argument must be an instance restriction (a type restriction can never
/// hold for null), or the DLR re-binds forever. Run with a timeout so a regression fails rather
/// than hangs the suite.
let private within (ms: int) (f: unit -> 'a) : 'a =
    // No second thread on wasm: run directly there (the JIT legs still catch a re-bind loop).
    if System.Environment.ProcessorCount < 2 then f ()
    else
        // A dedicated thread: not starved by the pool under load, and a spinner it leaves behind
        // does not occupy a pool thread.
        let t = System.Threading.Tasks.Task.Factory.StartNew(f, System.Threading.Tasks.TaskCreationOptions.LongRunning)
        let finished = try t.Wait ms with :? System.AggregateException as e -> raise e.InnerException   // the block's own exception
        if finished then t.Result else failwith "timed out: the site is re-binding forever"

[<Fact>]
let ``a null argument to an F# function member binds, and does not re-bind forever`` () =
    let h = box (Holders())
    within 5000 (fun () -> (dlr { return h?Label("x") } : string)) |> should equal "x"
    within 5000 (fun () -> (dlr { return h?Label(null) } : string)) |> should equal "null"            // an untyped null is obj: fits a string domain
    within 5000 (fun () -> (dlr { return h?Label(null: string) } : string)) |> should equal "null"
    within 5000 (fun () -> (dlr { return h?Label("y") } : string)) |> should equal "y"                // the site keeps both rules
    let label = box (fun (s: string) -> if isNull s then "null" else s)
    within 5000 (fun () -> (dlr { return label |> Dlr.apply (null: string) } : string)) |> should equal "null"
    within 5000 (fun () -> (dlr { return label |> Dlr.apply null } : string)) |> should equal "null"
    // A null does not fit a `unit -> R` function: a one-argument call must not bind a
    // zero-argument function because the argument happened to be null.
    let thunk = box (fun () -> "thunk")
    (fun () -> within 5000 (fun () -> (dlr { return thunk |> Dlr.apply null } : string)) |> ignore)
    |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``a unit-typed variable as the argument means no arguments`` () =
    let w: obj = Widget()
    let args = ()
    (dlr { return w?Describe(args) } : string) |> should equal "described"
    let f = box (fun () -> 42)
    within 5000 (fun () -> (dlr { return f |> Dlr.apply args } : int)) |> should equal 42

/// `Dlr.call x` read at a function type is the target itself as that function — the value's
/// counterpart of `x?Name` read as a function — and applied it is a call, like `(x?Name)(a)`.

[<Fact>]
let ``an exception through a read past five arrives as itself, as at five`` () =
    // The functions built at run time invoke the site with DynamicInvoke, which wraps what is thrown
    // in a TargetInvocationException; the typed helpers (five and under) never did.
    let t = box (Throwers())
    let five: int * int * int * int * int -> int = dlr { return t?Five }
    let six: int * int * int * int * int * int -> int = dlr { return t?Six }
    let sixCurried: int -> int -> int -> int -> int -> int -> int = dlr { return t?Six }
    (fun () -> five (1, 2, 3, 4, 5) |> ignore) |> should throw typeof<InvalidOperationException>
    (fun () -> six (1, 2, 3, 4, 5, 6) |> ignore) |> should throw typeof<InvalidOperationException>
    (fun () -> sixCurried 1 2 3 4 5 6 |> ignore) |> should throw typeof<InvalidOperationException>
    let missing: int * int * int * int * int * int -> int = dlr { return t?NoSuchMember }        // a binder miss, at the call
    let missingCurried: int -> int -> int -> int -> int -> int -> int = dlr { return t?NoSuchMember }
    (fun () -> missing (1, 2, 3, 4, 5, 6) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> missingCurried 1 2 3 4 5 6 |> ignore) |> should throw typeof<RuntimeBinderException>
