[<ReflectedDefinition>]
module Tests.Cache

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

// These tests share the global cache, so they are careful to only assert on deltas.

[<Fact>]
let ``same site compiles once for many calls`` () =
    let w = box (Widget())
    let f (i: int) : int = dlr { return w?Add(i, 1) }
    f 0 |> ignore
    let before = DlrCache.count ()
    for i in 1 .. 1000 do f i |> should equal (i + 1)
    DlrCache.count () |> should equal before

[<Fact>]
let ``different closure values reuse the delegate`` () =
    let f (o: obj) : string = dlr { return o?Pick(1) }
    f (Widget()) |> should equal "int"
    let before = DlrCache.count ()
    f (Widget()) |> should equal "int"
    DlrCache.count () |> should equal before

[<Fact>]
let ``clear forces recompilation`` () =
    let w = box (Widget())
    let f () : int = dlr { return w?Count }
    f () |> should equal 3
    DlrCache.clear ()
    f () |> should equal 3

[<Fact>]
let ``mutable capture reads the current value`` () =
    let w = box (Widget())
    let mutable n = 1
    let f () : int = dlr { return w?Add(n, 1) }
    f () |> should equal 2
    n <- 10
    f () |> should equal 11

let genericPick (w: obj) (x: 'a) : string = dlr { return w?Pick(x) }

[<Fact>]
let ``generic enclosing function: each instantiation is its own site with concrete types`` () =
    let w = Widget()
    let before = DlrCache.count ()
    genericPick w 1 |> should equal "int"
    genericPick w "s" |> should equal "string"
    genericPick w 2.5 |> should equal "obj"
    DlrCache.count () |> should equal (before + 3)
    genericPick w 7 |> should equal "int"
    genericPick w "t" |> should equal "string"
    DlrCache.count () |> should equal (before + 3)

[<Fact>]
let ``values the optimizer inlines instead of capturing still resolve`` () =
    // In Release, F# inlines constant locals and local functions into the closure instead of
    // capturing them; the reflected body still names them, so they resolve from their let binding.
    let w = box (Widget())
    let five = 5
    let nothing: obj = null
    let greeting () = "Hi"
    (dlr { return w?Add(five, five) } : int) |> should equal 10
    (dlr { return w?Text(nothing) } : string) |> should equal "string"
    (dlr { return w?Greet(greeting (), "you") } : string) |> should equal "Hi, you"
    // A lambda applied on the spot is beta-reduced the same way: its parameter is the argument.
    (fun (k: int) -> (dlr { return w?Add(k, 1) } : int)) 41 |> should equal 42
    (fun (a: int) (b: string) -> (dlr { return w?Greet(b, string a) } : string)) 7 "Hi" |> should equal "Hi, 7"

/// A `let rec` local function the Release optimizer inlines into the block: recovered as its
/// whole group (#198).
let private recursiveLocal (o: obj) (seed: int) : int =
    let x = seed
    let rec f n = if n = 0 then x else f (n - 1)
    let x = seed * 100
    dlr { return o?Add(f 2, x) }

let private mutuallyRecursiveLocals (o: obj) (seed: int) : int =
    let k = seed + 1
    let rec even n = if n = 0 then k else odd (n - 1)
    and odd n = if n = 0 then -k else even (n - 1)
    dlr { return o?Add(even 4, odd 3) }

/// A sibling's name shadowed after the group: the group's own `g` stays bound inside it.
let private recursiveSiblingShadowed (o: obj) (seed: int) : int =
    let rec f n = if n = 0 then 0 else g (n - 1)
    and g n = f n + 1
    let g = seed * 3
    dlr { return o?Add(f 2, g) }

[<Fact>]
let ``a recursive local function the optimizer inlines still resolves`` () =
    let w = box (Widget())
    recursiveLocal w 7 |> should equal 707
    mutuallyRecursiveLocals w 7 |> should equal 16
    recursiveSiblingShadowed w 7 |> should equal 23

/// A shadowed name inside a local function the Release optimizer inlines: the machine captures
/// only the later `x`, and the earlier one, reached through `f`'s recovered definition, read that
/// field and returned 1400 (#196).
let private shadowedThroughFunction (o: obj) (seed: int) : int =
    let x = seed
    let f () = x
    let x = seed * 100
    dlr { return o?Add(f (), x) }

let private shadowedThroughLambda (o: obj) (seed: int) : int =
    let x = seed
    let g = fun () -> x
    let x = seed * 100
    dlr { return o?Add(g (), x) }

/// Neither shadowed `x` is the block's own: each is reached through a function.
let private shadowedThroughTwoFunctions (o: obj) (seed: int) : int =
    let x = seed
    let f () = x
    let x = seed * 100
    let g () = x
    dlr { return o?Add(f (), g ()) }

/// A local function writing a shadowed mutable: if the optimizer inlines it, the write must not
/// land in the later `x`'s cell.
let private shadowedWrite (o: obj) : int =
    let mutable x = 1
    let set () = x <- 5
    let mutable x = 100
    // The analyzer reports it at build time (DLR007).
    // fsharpanalyzer: ignore-line-next DLR007
    dlr { set (); return o?Add(x, 0) }

/// Both shadowed `x`s are real values, so the machine holds both, as `x` and `x0`: which is which
/// is the compiler's to say.
let private shadowedBothCaptured (o: obj) : int =
    let x = Ticks.Next()
    let f () = x
    let x = Ticks.Next() * 100
    // The analyzer reports it at build time (DLR007).
    // fsharpanalyzer: ignore-line-next DLR007
    dlr { return o?Add(f (), x) }

/// `x` is also an unrelated lambda's parameter: nothing the block reaches shares the name.
let private nameOfAnUnrelatedLambda (o: obj) : int =
    let x = Ticks.Next()
    let f () = x
    let ys = [ 1 ] |> List.map (fun x -> x + 1)
    dlr { return o?Add(f (), ys.Length) }

let private mutableNameOfAnUnrelatedLambda (o: obj) : int =
    let mutable n = 5
    let get () = n
    let ys = [ 1 ] |> List.map (fun n -> n + 1)
    dlr { return o?Add(get (), ys.Length) }

/// An alias of a mutable is a value of its own, taken when it is bound: the optimizer keeps it.
let private aliasOfMutable (o: obj) : int =
    let mutable y = 1
    let x = y
    let f () = x
    y <- 2
    dlr { return o?Add(f (), y) }

let private aliasOfAnAliasOfMutable (o: obj) : int =
    let mutable m = 1
    let y = m
    let x = y
    let f () = x
    m <- 2
    dlr { return o?Add(f (), m) }

let private aliasOfMutableShadowed (o: obj) : int =
    let mutable y = 1
    let x = y
    let f () = x
    y <- 2
    let x = 100
    // The analyzer reports it at build time (DLR007).
    // fsharpanalyzer: ignore-line-next DLR007
    dlr { return o?Add(x, f ()) }

[<Fact>]
let ``an alias of a mutable reached through a local function keeps the value it was bound to`` () =
    let w = box (Widget())
    aliasOfMutable w |> should equal 3
    aliasOfAnAliasOfMutable w |> should equal 3
    match (try Ok(aliasOfMutableShadowed w) with :? DlrTranslationException as e -> Error e.Message) with
    | Ok n -> n |> should equal 101
    | Error message -> message |> should haveSubstring "reaches two variables named 'x'"

[<Fact>]
let ``a shadowed name reached through an inlined local function reads its own value`` () =
    let w = box (Widget())
    shadowedThroughFunction w 7 |> should equal 707
    shadowedThroughLambda w 7 |> should equal 707
    shadowedThroughTwoFunctions w 7 |> should equal 707

[<Fact>]
let ``a write to a shadowed mutable through a local function never lands in the other variable`` () =
    // Release inlines `set` and must refuse the ambiguous write; Debug calls it and is right.
    match (try Ok(shadowedWrite (box (Widget()))) with :? DlrTranslationException as e -> Error e.Message) with
    | Ok n -> n |> should equal 100
    | Error message -> message |> should haveSubstring "rename one"

[<Fact>]
let ``two real values of one name, one through an inlined local function, are refused rather than guessed`` () =
    // Release inlines `f`; Debug captures it and is right.
    Ticks.Reset()
    match (try Ok(shadowedBothCaptured (box (Widget()))) with :? DlrTranslationException as e -> Error e.Message) with
    | Ok n -> n |> should equal 201
    | Error message -> message |> should haveSubstring "reaches two variables named 'x'"

[<Fact>]
let ``a name an unrelated lambda also uses is still read from its field`` () =
    let w = box (Widget())
    Ticks.Reset()
    nameOfAnUnrelatedLambda w |> should equal 2
    Ticks.Reset()
    nameOfAnUnrelatedLambda w |> should equal 2
    mutableNameOfAnUnrelatedLambda w |> should equal 6

[<Fact>]
let ``clear then a call recompiles`` () =
    let w = box (Widget())
    let read () : int = dlr { return w?Count }
    read () |> should equal 3
    DlrCache.clear ()
    let before = DlrCache.count ()
    read () |> should equal 3
    DlrCache.count () - before |> should equal 1          // recompiled, not served from a stale typed entry

[<Fact>]
let ``captured variables named like the state machine's own fields still resolve`` () =
    // The compiled block's struct has `Data` and `ResumptionPoint` fields of its own, which the
    // translator must not resolve a variable to. `Data`: the closure gets a second field of that
    // name (of the local's type). `ResumptionPoint`: the optimizer inlines the literal, so there is
    // no field and the variable resolves from the enclosing member, not to the machine's counter
    // (which would read 0). An `int` of that name the optimizer keeps is a compiler error in
    // Release, as in task { }; a `let mutable` is an FSharpRef field and clashes with nothing.
    let Data = box (Widget())
    let ResumptionPoint = 2
    (dlr { return Data?Add(ResumptionPoint, 1) } : int) |> should equal 3
    let mutable ResumptionPoint = 5
    dlr { ResumptionPoint <- Data?Add(ResumptionPoint, 1) }
    ResumptionPoint |> should equal 6
