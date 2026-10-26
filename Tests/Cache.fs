[<ReflectedDefinition>]
module Tests.Cache

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
#if DLR_CLOSURES
open Tests.ClosureBuilder
#endif

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

let private needsStruct<'T when 'T : struct> () = Unchecked.defaultof<'T>
let private needsDisposable<'T when 'T :> IDisposable> (x: 'T) = x
let constrainedUnused<'T when 'T : struct> (w: obj) : int =
    let k = needsStruct<'T> ()
    ignore k
    dlr { return w?Count }
let constrainedBeside<'T, 'U when 'U : struct> (w: obj) (x: 'T) : string =
    let k = needsStruct<'U> ()
    ignore k
    dlr { return w?Pick(x) }
let disposableUnused<'T when 'T :> IDisposable> (w: obj) (d: 'T) : int =
    ignore (needsDisposable d)
    dlr { return w?Count }

/// A type parameter the block's container does not carry, with a constraint `obj` cannot meet,
/// still compiles; one it does carry keeps its concrete type (#222).
[<Fact>]
let ``generic enclosing function: a constrained type parameter the block does not use`` () =
    let w = Widget()
    constrainedUnused<int> w |> should equal 3
    constrainedBeside<int, DateTime> w 1 |> should equal "int"
    constrainedBeside<string, int> w "s" |> should equal "string"
    disposableUnused w (new IO.MemoryStream()) |> should equal 3

/// A generic type's parameter is carried only where the closure uses it, so a member of a
/// constrained generic type is the same case through the declaring type (#222).
type StructHolder<'T when 'T : struct>() =
    static member Count(w: obj) : int = dlr { return w?Count }
    member _.Instance(w: obj) : int = dlr { return w?Count }
    member _.Pick(w: obj, x: 'U) : string = dlr { return w?Pick(x) }

[<Fact>]
let ``generic enclosing type: a constrained type parameter the block does not use`` () =
    let w = Widget()
    StructHolder<int>.Count w |> should equal 3
    StructHolder<DateTime>().Instance w |> should equal 3
    StructHolder<int>().Pick(w, 1) |> should equal "int"
    StructHolder<int>().Pick(w, "s") |> should equal "string"

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

/// A value piped into a lambda holding a block is the same application, `x |> (fun p -> …)`:
/// the optimizer beta-reduces it, so `p` is the piped value and is never captured (#252).
let private pipedInto (o: obj) : int = o |> (fun x -> dlr { return x?Count })
let private pipedBack (o: obj) : int = (fun x -> dlr { return x?Count }) <| o
/// The same into a once-called local function, `x |> g`: its single application supplies `x`.
let private pipedIntoLocal (o: obj) : int =
    let g (x: obj) : int = dlr { return x?Count }
    o |> g
let private pipedBackIntoLocal (o: obj) : int =
    let g (x: obj) : int = dlr { return x?Count }
    g <| o

/// A block in a match arm whose or-pattern binds a variable: the quotation holds the arm's body once
/// per alternative, so the block is found once per alternative on its line; the copies are one block
/// (#263), with binders inside it, in generic and class members, nested, over tuples and unions.
let private orPatternBound (o: obj) (c: Choice<int, int>) : int =
    match c with
    | Choice1Of2 x | Choice2Of2 x -> dlr { return o?Echo(x) }
let private orPatternThree (o: obj) (c: Choice<int, int, int>) : int =
    match c with
    | Choice1Of3 x | Choice2Of3 x | Choice3Of3 x -> dlr { return o?Add(x, 1) }
let private orPatternUnbound (o: obj) (c: Choice<int, string>) : int =
    match c with
    | Choice1Of2 _ | Choice2Of2 _ -> dlr { return o?Count }
let private orPatternLet (o: obj) (c: Choice<int, int>) : int =
    match c with
    | Choice1Of2 x | Choice2Of2 x -> dlr { let y = x + 1 in return o?Echo(y) }
let private orPatternLoop (o: obj) (c: Choice<int, int>) : int =
    match c with
    | Choice1Of2 x | Choice2Of2 x ->
        dlr {
            let mutable total = 0
            for i in 1 .. x do total <- total + (o?Add(i, 0) : int)
            return total
        }
let private orPatternGuard (o: obj) (c: Choice<int, int>) : int =
    match c with
    | Choice1Of2 x | Choice2Of2 x when x > 0 -> dlr { return o?Echo(x) }
    | _ -> -1
let private orPatternNested (o: obj) (c: Choice<Choice<int, int>, int>) : int =
    match c with
    | Choice1Of2 (Choice1Of2 x | Choice2Of2 x) | Choice2Of2 x -> dlr { return o?Echo(x) }
let private orPatternTwoVars (o: obj) (c: int * Choice<int, int>) : int =
    match c with
    | (x, Choice1Of2 y) | (x, Choice2Of2 y) -> dlr { return o?Add(x, y) }
let private orPatternFunction (o: obj) : Choice<int, int> -> int =
    function Choice1Of2 x | Choice2Of2 x -> dlr { return o?Echo(x) }
let private orPatternGeneric<'T> (o: obj) (c: Choice<'T, 'T>) : 'T =
    match c with
    | Choice1Of2 x | Choice2Of2 x -> dlr { return o?Echo(x) }
let private orPatternTuple (o: obj) (c: Choice<int * int, int * int>) : int =
    match c with
    | Choice1Of2 x | Choice2Of2 x -> dlr { return o?Add(fst x, snd x) }
type OrPatternCase = OrA of int * int | OrB of int * int
let private orPatternUnion (o: obj) (c: OrPatternCase) : int =
    match c with
    | OrA (p, q) | OrB (p, q) -> dlr { return o?Add(p, q) }
/// A captured variable named like the machine's own `Data` field (two fields of that name).
let private orPatternCapturesData (c: Choice<int, int>) : int =
    let Data: obj = box (Widget())
    match c with
    | Choice1Of2 x | Choice2Of2 x -> dlr { return Data?Echo(x) }
type OrPatternMembers(o: obj) =
    member _.Pick(c: Choice<int, int>) : int =
        match c with
        | Choice1Of2 x | Choice2Of2 x -> dlr { return o?Echo(x) }
    member _.PickGeneric<'T>(c: Choice<'T, 'T>) : 'T =
        match c with
        | Choice1Of2 x | Choice2Of2 x -> dlr { return o?Echo(x) }
type OrPatternGenericType<'T>(o: obj) =
    member _.Pick(c: Choice<'T, 'T>) : 'T =
        match c with
        | Choice1Of2 x | Choice2Of2 x -> dlr { return o?Echo(x) }

[<Fact>]
let ``a block in an or-pattern's arm is one block, not one per alternative`` () =
    let w = box (Widget())
    orPatternBound w (Choice1Of2 4) |> should equal 4
    orPatternBound w (Choice2Of2 5) |> should equal 5
    orPatternThree w (Choice3Of3 6) |> should equal 7
    orPatternUnbound w (Choice2Of2 "s") |> should equal 3
    orPatternLet w (Choice2Of2 5) |> should equal 6
    orPatternLoop w (Choice2Of2 4) |> should equal 10
    (orPatternGuard w (Choice1Of2 4), orPatternGuard w (Choice2Of2 -5)) |> should equal (4, -1)
    (orPatternNested w (Choice1Of2 (Choice2Of2 2)), orPatternNested w (Choice2Of2 3)) |> should equal (2, 3)
    orPatternTwoVars w (3, Choice2Of2 4) |> should equal 7
    orPatternFunction w (Choice2Of2 5) |> should equal 5
    (orPatternGeneric<int> w (Choice1Of2 4), orPatternGeneric<string> w (Choice2Of2 "b")) |> should equal (4, "b")
    orPatternTuple w (Choice2Of2 (3, 4)) |> should equal 7
    orPatternUnion w (OrB (3, 4)) |> should equal 7
    OrPatternMembers(w).Pick(Choice2Of2 5) |> should equal 5
    OrPatternMembers(w).PickGeneric<string>(Choice2Of2 "b") |> should equal "b"
    OrPatternGenericType<string>(w).Pick(Choice2Of2 "b") |> should equal "b"
    orPatternCapturesData (Choice2Of2 5) |> should equal 5

/// Look-alike blocks on one line in different scopes, over same-named variables, are not copies of
/// one block: whichever way the compiler built them, each gives its own value or is refused, never
/// the other's (#263). The analyzer reports two blocks on a line at build time (DLR003).
let private lookAlikeTuples (o: obj) (a: int) : int * int =
    // fsharpanalyzer: ignore-line-next DLR003
    (let x = (a, 1) in (dlr { return o?Add(fst x, snd x) } : int)), (let x = (a, 2) in (dlr { return o?Add(fst x, snd x) } : int))
let private lookAlikeValues (o: obj) (a: int) (b: int) : int * int =
    // fsharpanalyzer: ignore-line-next DLR003
    (let x = a in (dlr { return o?Echo(x) } : int)), (let x = b in (dlr { return o?Echo(x) } : int))
let private lookAlikeComputed (o: obj) (a: int) : int * int =
    // fsharpanalyzer: ignore-line-next DLR003
    (let x = a + 1 in (dlr { return o?Echo(x) } : int)), (let x = a + 2 in (dlr { return o?Echo(x) } : int))
let private lookAlikeLets (o: obj) (a: int) (b: int) : int * int =
    // fsharpanalyzer: ignore-line-next DLR003
    (let x = a in (dlr { let y = x in return o?Echo(y) } : int)), (let x = b in (dlr { let y = x in return o?Echo(y) } : int))

/// The same named like a state machine's own fields, which are not captures (#266 review).
let private lookAlikeNamedData (o: obj) (a: int) : int * int =
    // fsharpanalyzer: ignore-line-next DLR003
    (let Data = (a, 1) in (dlr { return o?Add(fst Data, snd Data) } : int)), (let Data = (a, 2) in (dlr { return o?Add(fst Data, snd Data) } : int))
let private lookAlikeNamedResumptionPoint (o: obj) (a: int) : int * int =
    // fsharpanalyzer: ignore-line-next DLR003
    (let ResumptionPoint = (a, 1) in (dlr { return o?Add(fst ResumptionPoint, snd ResumptionPoint) } : int)), (let ResumptionPoint = (a, 2) in (dlr { return o?Add(fst ResumptionPoint, snd ResumptionPoint) } : int))

[<Fact>]
let ``look-alike blocks on one line give their own values or are refused`` () =
    let w = box (Widget())
    let ownOrRefused (expected: int * int) (f: unit -> int * int) =
        match (try Choice1Of2(f ()) with :? DlrTranslationException as e -> Choice2Of2 e) with
        | Choice1Of2 values -> values |> should equal expected
        | Choice2Of2 _ -> ()
    ownOrRefused (11, 12) (fun () -> lookAlikeTuples w 10)
    ownOrRefused (1, 2) (fun () -> lookAlikeValues w 1 2)
    ownOrRefused (11, 12) (fun () -> lookAlikeComputed w 10)
    ownOrRefused (1, 2) (fun () -> lookAlikeLets w 1 2)
    ownOrRefused (11, 12) (fun () -> lookAlikeNamedData w 10)
    ownOrRefused (11, 12) (fun () -> lookAlikeNamedResumptionPoint w 10)

/// Two different blocks on one line are still refused: only copies of one block are collapsed.
let private twoBlocksOneLine (o: obj) : int * int =
    // fsharpanalyzer: ignore-line-next DLR003
    (dlr { return o?Count } : int), (dlr { return o?Add(1, 2) } : int)

/// An or-pattern arm whose block reads a variable the Release optimizer does not capture as
/// itself (`whole`, substituted by the matched value) is still one block: it runs, or it is refused
/// as such, not as two blocks on a line.
let private orPatternAs (o: obj) (c: Choice<int, int>) : string =
    match c with
    | (Choice1Of2 x | Choice2Of2 x) as whole -> dlr { return o?Echo(sprintf "%d %A" x whole) }

[<Fact>]
let ``an or-pattern block that cannot be resolved says so, not that two blocks share a line`` () =
    let w = box (Widget())
    match (try Choice1Of2(orPatternAs w (Choice2Of2 5)) with :? DlrTranslationException as e -> Choice2Of2 e.Message) with
    | Choice1Of2 value -> value |> should equal "5 Choice2Of2 5"
    | Choice2Of2 message ->
        message |> should haveSubstring "is held 2 times"
        message |> should haveSubstring "'whole'"
        message |> should not' (haveSubstring "put each dlr { } on its own line")

[<Fact>]
let ``two different blocks on one line are still refused`` () =
    // The analyzer reports this at build time (DLR003); this pins the run-time error behind it.
    let e = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> twoBlocksOneLine (Widget()) |> ignore)
    e.Message |> should haveSubstring "2 dlr { } blocks share"

[<Fact>]
let ``a value piped into a lambda or local function holding a block resolves`` () =
    let w = Widget()
    pipedInto w |> should equal 3
    pipedBack w |> should equal 3
    pipedIntoLocal w |> should equal 3
    pipedBackIntoLocal w |> should equal 3
    let o = box w
    (41 |> fun (k: int) -> (dlr { return o?Add(k, 1) } : int)) |> should equal 42

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

/// A tuple the block reads whole: Release splits it into a field per element (`t_0`, `t_1`),
/// and its definition must not run again (#203).
let private wholeTuple (o: obj) : int =
    let t = (Ticks.Next(), Ticks.Next() * 10)
    dlr { return o?Add(fst t, snd t) }

/// One element a constant: the optimizer inlines it and gives it no field.
let private tupleWithAConstant (o: obj) : int =
    let t = (Ticks.Next(), 70)
    dlr { return o?Add(fst t, snd t) }

/// Built in an `if`: the elements become mutable locals, `t_0$tupleElem`.
let private branchingTuple (o: obj) : int =
    let t = if Ticks.Next() > 0 then (Ticks.Next(), 70) else (0, 0)
    dlr { return o?Add(fst t, snd t) }

/// Built behind lets: its elements are fields under their own names, and its definition runs
/// `Ticks.Next()` again, so it is refused rather than recovered.
let private tupleBehindLets (o: obj) : int =
    let t = (let a = Ticks.Next() in let b = 70 in (a, b))
    dlr { return o?Add(fst t, snd t) }

/// Only its first element read: Release keeps only `t_0`, which `fst t` reads.
let private firstOfATuple (o: obj) : int =
    let t = (Ticks.Next(), Ticks.Next() * 10)
    dlr { return o?Add(fst t, 0) }

[<Fact>]
let ``a tuple the block reads whole is not built again`` () =
    let w = box (Widget())
    Ticks.Reset()
    wholeTuple w |> should equal 21
    Ticks.Reset()
    firstOfATuple w |> should equal 1
    Ticks.Reset()
    tupleWithAConstant w |> should equal 71
    Ticks.Reset()
    branchingTuple w |> should equal 72
    Ticks.Reset()
    match (try Ok(tupleBehindLets w) with :? DlrTranslationException as e -> Error e.Message) with
    | Ok n -> n |> should equal 71
    | Error message -> message |> should haveSubstring "would run the definition of 't' again"

/// A tuple built from another tuple's element: Release substitutes `t_0` by `u_1`, a field of
/// another name, so `t_0` has no field though it is read (found reviewing an IL-alignment spike, #200).
let private elementOfAnotherTuple (o: obj) (u: int * int) : string =
    let t = (snd u, Ticks.Next())
    dlr { return o?Echo(fst t, snd t) }

let private elementOfAnotherLocalTuple (o: obj) (seed: int) : string =
    let u = (seed * 10 + 1, Ticks.Next())
    let t = (fst u, Ticks.Next())
    dlr { return o?Echo(fst t, snd t) }

/// An element the block binds itself: Release captures it under the block's name, `b`.
let private elementBoundInTheBlock (o: obj) (seed: int) : string =
    let t = (seed * 10 + 1, Ticks.Next())
    dlr {
        let b = snd t
        return o?Echo(fst t, b) }

/// A user's `x1` beside a shared `x` (whose fields are `x`, `x0`, …), and an inlined function's
/// parameter `x` applied to a constant, which the IL never reads (found reviewing an IL-alignment spike, #200).
let private userX1BesideSharedX (o: obj) (seed: int) : string =
    let f x = x + 1
    let x = seed * 10 + 1
    let h () = x
    let x = seed * 10 + 2
    let x1 = seed * 10 + 3
    // The analyzer reports it at build time (DLR007).
    // fsharpanalyzer: ignore-line-next DLR007
    dlr { return o?Echo(f 5, h (), x, x1) }

/// A shared `x` beside a captured `xs`: a name `x` begins, not one of its fields.
let private sharedXBesideXs (o: obj) (seed: int) : string =
    let x = seed * 10 + 1
    let h () = x
    let x = seed * 10 + 2
    let xs = seed * 10 + 3
    // The analyzer reports it at build time (DLR007).
    // fsharpanalyzer: ignore-line-next DLR007
    dlr { return o?Echo(h (), x, xs) }

type private Pair = { A: int; B: int }

/// A pattern-bound `x` (split later, so its element field is `x_00`) and a record whose fields are
/// written B then A: the optimizer reorders the two pure reads, and the declaration order agreed
/// with the swapped pairing by coincidence (found reviewing an IL-alignment spike, #200).
let private patternBoundReadsReordered (o: obj) (seed: int) : string =
    match Some((seed * 10 + 1, 70)) with
    | Some x ->
        let f1 () = x
        let x = (Ticks.Next() * 1000 + 2, 71)
        // The analyzer reports it at build time (DLR007).
        // fsharpanalyzer: ignore-line-next DLR007
        dlr {
            let r = { B = fst (f1 ()); A = fst x }
            return o?Echo(r.A, r.B) }
    | None -> "none"

/// A user's `t_0` beside a tuple `t` the optimizer splits: the split element gets `t_00`, and
/// `t_0` is the user's (Copilot on #211).
let private userT0BesideSplitTuple (o: obj) : string =
    let t = (Ticks.Next(), 70)
    let t_0 = 999
    dlr { return o?Echo(fst t, t_0) }

/// A split element the optimizer names after the block's own binding (`a`, not `t_0`), from a
/// mutable changed since: rebuilt from the definition it would read the mutable's current value.
let private elementOfAMutableRenamedInTheBlock (o: obj) : string =
    let mutable m = Ticks.Next()
    let t = (m, 70)
    m <- 99
    dlr {
        let a = fst t
        return o?Echo(a, m) }

/// The `t_0` collision in the one Release shape that takes the closure path.
let private userT0BesideSplitTupleClosure (o: obj) : string =
    let t = (Ticks.Next(), 70)
    let t_0 = 999
    (dlr { return fun () -> o?Echo(fst t, t_0) } : unit -> string) ()

/// An alias of a module's immutable values: the optimizer folds them into the block, and reading
/// them again runs nothing.
let private aliasOfModuleValues (o: obj) : string =
    let n = CaptureConstants.answer
    let s = CaptureConstants.name
    dlr { return o?Echo(n, s.Length) }

[<Fact>]
let ``an alias of a module's immutable value the optimizer folds still resolves`` () =
    aliasOfModuleValues (box (CaptureEcho())) |> should equal "42|3"

[<Fact>]
let ``what the optimizer did to a tuple or a shared name is never guessed`` () =
    let echo = box (CaptureEcho())
    let check (expected: string) (run: unit -> string) =
        Ticks.Reset()
        match (try Ok(run ()) with :? DlrTranslationException as e -> Error e.Message) with
        | Ok value -> value |> should equal expected
        | Error _ -> ()
    check "31|1" (fun () -> elementOfAnotherTuple echo (0, 31))
    check "31|2" (fun () -> elementOfAnotherLocalTuple echo 3)
    check "31|1" (fun () -> elementBoundInTheBlock echo 3)
    check "6|31|32|33" (fun () -> userX1BesideSharedX echo 3)
    check "31|32|33" (fun () -> sharedXBesideXs echo 3)
    check "1002|71" (fun () -> patternBoundReadsReordered echo 7)
    check "1|999" (fun () -> userT0BesideSplitTuple echo)
    check "1|99" (fun () -> elementOfAMutableRenamedInTheBlock echo)
    check "1|999" (fun () -> userT0BesideSplitTupleClosure echo)
#if DEBUG
    // Debug splits no tuple: nothing to refuse.
    Ticks.Reset()
    userT0BesideSplitTuple echo |> should equal "1|999"
#endif

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

/// A failure the translator wraps (here the tree hook throwing, as compiling the tree might run
/// out of stack or memory) is not a refusal: it is not kept, and the next call compiles (#214).
[<Fact>]
let ``a failure compiling a block is retried, not kept as a refusal`` () =
    let w = box (Widget())
    let sink = typeof<DlrRun>.Assembly.GetType("FSharp.Interop.Dlr.Translate+TreeHook").GetProperty("Sink", Reflection.BindingFlags.NonPublic ||| Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Static)
    let read () : int = dlr { return w?Count }
    DlrCache.clear ()
    try
        sink.SetValue(null, Action<Type, Linq.Expressions.LambdaExpression>(fun _ _ -> raise (InsufficientExecutionStackException())))
        let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> read () |> ignore)
        ex.InnerException |> should be instanceOfType<InsufficientExecutionStackException>
    finally
        sink.SetValue(null, null)
    read () |> should equal 3

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
    // (which would read 0). An `int` of that name the machine keeps is a compiler error (FS2014),
    // as in task { }: in Release when the optimizer keeps it, and in Debug on the compilers that
    // build the machine there too (DEBUG_MACHINES) — so that case is not compiled for them. A `let mutable`
    // is an FSharpRef field and clashes with nothing.
    let Data = box (Widget())
#if !(DEBUG && DEBUG_MACHINES)
    let ResumptionPoint = 2
    (dlr { return Data?Add(ResumptionPoint, 1) } : int) |> should equal 3
#endif
    let mutable ResumptionPoint = 5
    dlr { ResumptionPoint <- Data?Add(ResumptionPoint, 1) }
    ResumptionPoint |> should equal 6
