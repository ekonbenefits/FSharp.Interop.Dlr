/// Blocks with no [<ReflectedDefinition>] anywhere: the build companion's map holds their members'
/// bodies (`FSharp.Interop.Dlr.Build/Bodies.fs`, the 2.0 build companion, #259). Without a map
/// embedded (`-p:DlrCompanion=true`) they skip.
module Tests.Companion

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
#if DLR_CLOSURES
open Tests.ClosureBuilder
#endif

/// Whether this assembly carries a map with block bodies (`-p:DlrCompanion=true`): format 2, a text
/// header of `F offset length file` lines, then each file's blob, deflated.
let bodiesMapped =
    use s = typeof<Widget>.Assembly.GetManifestResourceStream "FSharp.Interop.Dlr.CaptureMap"
    if isNull s then false
    else
        use m = new System.IO.MemoryStream()
        s.CopyTo m
        let bytes = m.ToArray()
        let text = System.Text.Encoding.UTF8.GetString bytes
        match text.IndexOf "\n\n" with
        | -1 -> false
        | header ->
            let start = System.Text.Encoding.UTF8.GetByteCount(text.Substring(0, header + 2))
            text.Substring(0, header).Split '\n'
            |> Array.exists (fun line ->
                match line.Split '\t' with
                | [| "F"; offset; length; _ |] ->
                    use z = new System.IO.Compression.DeflateStream(new System.IO.MemoryStream(bytes, start + int offset, int length), System.IO.Compression.CompressionMode.Decompress)
                    (new System.IO.StreamReader(z)).ReadToEnd().Contains "S\tbody"
                | _ -> false)

let private needsBodies () =
    if not bodiesMapped then raise (AnyUnit.IgnoreException "no block bodies embedded (-p:DlrCompanion=true)")

/// Beside the blocks, what a module-wide attribute could not take: a byref parameter.
let bump (x: byref<int>) = x <- x + 1

let private count (w: obj) : int =
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return w?Count }

let private countPlus (w: obj) (k: int) : int =
    let doubled = k * 2
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return w?Count + doubled }

let private generic<'T> (w: obj) : 'T =
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return w?Count }

type private Reader(w: obj) =
    member _.Count : int =
        // fsharpanalyzer: ignore-line-next DLR001
        dlr { return w?Count }

[<Fact>]
let ``a block with no reflected definition runs from the companion's body`` () =
    needsBodies ()
    count (Widget()) |> should equal 3

[<Fact>]
let ``its captures bind as they do from a reflected definition`` () =
    needsBodies ()
    countPlus (Widget()) 4 |> should equal 11

[<Fact>]
let ``a generic member's block takes its instantiation`` () =
    needsBodies ()
    generic<int64> (Widget()) |> should equal 3L

[<Fact>]
let ``a class member's block`` () =
    needsBodies ()
    Reader(Widget()).Count |> should equal 3

[<Fact>]
let ``the module also holds a byref member, which a module-wide attribute would refuse`` () =
    let mutable n = 1
    bump &n
    n |> should equal 2

// ---- Shapes the encoder must carry exactly or refuse (the cold review of #219).

/// A unit of measure in the member's signature: erased to its underlying type, as in the reflected
/// definition (`float<m>` is `System.Double`), not named as FSharp.Core's annotated abbreviation.
[<Measure>] type m

let private scaled (w: obj) (d: float<m>) : int =
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return w?Add(int (float d), 1) }

[<Fact>]
let ``a member with a unit of measure in its signature`` () =
    needsBodies ()
    let w: obj = Widget()
    let result = scaled w 2.0<m>
    result |> should equal 3

/// An F# exception, raised and caught by its field: FCS has its construction as a record's and its
/// field as a record field; the quotation, its constructor and its `Data0` property.
exception Carried of int

let private carried (w: obj) (k: int) : int =
    try raise (Carried(k * 10))
    with Carried n ->
        // fsharpanalyzer: ignore-line-next DLR001
        dlr { return w?Count + n }

[<Fact>]
let ``an F# exception raised and caught by its field`` () =
    needsBodies ()
    let w: obj = Widget()
    let result = carried w 2
    result |> should equal 23

/// A guard's variable: bound for the guard and again for its target, both in the quotation; the
/// block reads the target's (the field pass takes the binding in scope at the block; a handler's
/// variable, above, is the filter's and the handler's the same way).
let private guarded (w: obj) (k: int) : int =
    match Some(k * 10) with
    | Some x when x <> -1 ->
        // fsharpanalyzer: ignore-line-next DLR001
        dlr { return w?Count + x }
    | _ -> 0

[<Fact>]
let ``a guard's variable, bound for the guard and for its target`` () =
    needsBodies ()
    let w: obj = Widget()
    let result = guarded w 4
    result |> should equal 43

let inline private applyInline ([<InlineIfLambda>] h: unit -> 'T) : 'T = h ()

/// A guard over a tuple the optimizer splits, the block reading it whole through an inlined lambda:
/// only element 0 is kept, element 1 nowhere, and it is recovered from the literal (70). A guard
/// binds `x` twice; the element is the target's binding's, the one the block reads (#277, #278).
let private guardedTuple (w: obj) (k: int) : int =
    match Some(k * 10, 70) with
    | Some x when fst x <> -1 ->
        // fsharpanalyzer: ignore-line-next DLR001
        dlr { return w?Count + fst (applyInline (fun () -> x)) }
    | _ -> -1

[<Fact>]
let ``a guard over a split tuple read whole`` () =
    needsBodies ()
    let w: obj = Widget()
    let result = guardedTuple w 4
    result |> should equal 43

/// A split tuple passed whole into an inlined helper the optimizer folds: it drops element 1
/// because it knows its value (the literal's 70), not because nothing needs it, so the element is
/// recovered from the literal, never a placeholder (#278: 43 where plain F# gives 113).
type TupleRecord = { Pair: int * int }
type TupleCase = TupleCase of (int * int)
let inline private sndRecord (r: TupleRecord) : int = snd r.Pair
let inline private fstRecord (r: TupleRecord) : int = fst r.Pair
let inline private sndCase (TupleCase p) : int = snd p

let private viaRecord (w: obj) (k: int) : int =
    match Some(k * 10, 70) with
    | Some x ->
        // fsharpanalyzer: ignore-line-next DLR001
        dlr { let b = sndRecord { Pair = x } in return w?Count + fst x + b }
    | _ -> -1

let private viaCase (w: obj) (k: int) : int =
    match Some(k * 10, 70) with
    | Some x ->
        // fsharpanalyzer: ignore-line-next DLR001
        dlr { let b = sndCase (TupleCase x) in return w?Count + fst x + b }
    | _ -> -1

let private bothViaRecord (w: obj) (k: int) : int =
    match Some(k * 10, 70) with
    | Some x ->
        // fsharpanalyzer: ignore-line-next DLR001
        dlr { return w?Count + fstRecord { Pair = x } + sndRecord { Pair = x } }
    | _ -> -1

[<Fact>]
let ``a split tuple's dropped element is its literal's, never a placeholder`` () =
    needsBodies ()
    let w: obj = Widget()
    // Each the plain F# value (113 for 4) or refused, never another.
    let check (f: unit -> int) =
        match (try Choice1Of2(f ()) with :? DlrTranslationException as e -> Choice2Of2 e) with
        | Choice1Of2 value -> value |> should equal 113
        | Choice2Of2 _ -> ()
    check (fun () -> viaRecord w 4)
    check (fun () -> viaCase w 4)
    check (fun () -> bothViaRecord w 4)
    // The literal recovered, the first runs (no longer refused or a placeholder).
    let result = viaRecord w 4
    result |> should equal 113

/// Two guarded arms binding one name, the block in the second: the match compiler gives the
/// machine's field the first arm's value, which the field pass does not attribute (no entry: the
/// strict behaviour, which binds it by name), rather than guess between the arms.
let private secondGuard (w: obj) (k: int) : int =
    match Some(k * 10) with
    | Some x when x < 0 -> x
    | Some x when x > 0 ->
        // fsharpanalyzer: ignore-line-next DLR001
        dlr { return w?Count + x }
    | _ -> 0

[<Fact>]
let ``a second guarded arm binding the same name`` () =
    needsBodies ()
    let w: obj = Widget()
    let result = secondGuard w 4
    result |> should equal 43

/// A block in a `let rec` local function: the outer `x` reaches its machine as the closure's copy,
/// whose range is the whole `let rec`, which holds the inner `x`'s definition too. The copy is the
/// one `x` declared before the function, never the inner one by value (two fields once mapped to
/// the inner `x`, the outer recovered from a definition that reads `seed`: refused, #276).
let private recursiveTwoX (o: obj) (seed: int) : string =
    let x = seed * 10 + 1
    let f1 () = x
    let rec go (j: int) : string =
        if j > 0 then go (j - 1)
        else
            let x = seed * 10 + 2
            // fsharpanalyzer: ignore-line-next DLR001
            dlr { return o?Echo(f1 (), x) }
    go 1

/// The guard's `x` beside an outer one an inlined local function reads: the guard's field takes
/// the range of `seed * 10 + 2`, which the inlined `+`'s parameters (`arg0_0`) share. Those are no
/// closure, so the field is attributed by value, to the guard's `x`, not as a copy of the outer one.
let private guardedBesideOuter (o: obj) (seed: int) : string =
    let x = seed * 10 + 1
    let f1 () = x
    match Some(seed * 10 + 2) with
    | Some x when x <> -1 ->
        // fsharpanalyzer: ignore-line-next DLR001
        dlr { return o?Echo(f1 (), x) }
    | _ -> "none"

[<Fact>]
let ``a let rec local function's block reads the outer and the inner variable of one name`` () =
    needsBodies ()
    let o: obj = CaptureEcho()
    let result = recursiveTwoX o 4
    result |> should equal "41|42"
    let guarded = guardedBesideOuter o 4
    guarded |> should equal "41|42"

/// A function-typed block applied on the spot takes the closure path in Release too (#217): its
/// container is the block's delegate closure, which the map's `closure` section describes (#276),
/// so two `y`s, one through an inlined local function, bind exactly instead of being refused.
let private appliedTwoY (o: obj) (k: int) : int =
    let y = k + 1
    let g () = y
    let y = k + 100
    // fsharpanalyzer: ignore-line-next DLR001
    (dlr { return fun (z: int) -> o?Count + g () + y + z } : int -> int) 5

let private appliedCapturing (o: obj) (k: int) : int =
    let y = k + 1
    // fsharpanalyzer: ignore-line-next DLR001
    (dlr { return fun (z: int) -> o?Count + y + z } : int -> int) 5

let private appliedMutable (o: obj) (k: int) : int =
    let mutable m = k
    m <- m + 1
    // fsharpanalyzer: ignore-line-next DLR001
    (dlr { return fun (z: int) -> o?Count + m + z } : int -> int) 5

/// Two such blocks nested on one line: the closure section is the outer block's (the one the
/// inner's closure call is inside), not the inner's, whose closing brace comes first on the line
/// (it was, from #282's review: rejected, `no field y0`).
let private appliedNested (o: obj) (k: int) : string =
    let y = k + 1
    let g () = y
    // fsharpanalyzer: ignore-line-next DLR001
    (dlr { return fun (_: int) -> (let y = k + 2 in (dlr { return fun (w: int) -> (o?Echo(g (), y, w) : string) } : int -> string) 7) } : int -> string) 5

[<Fact>]
let ``a function-typed block applied on the spot binds through the closure section`` () =
    needsBodies ()
    let w: obj = Widget()
    let twoY = appliedTwoY w 4
    twoY |> should equal 117
    let capturing = appliedCapturing w 4
    capturing |> should equal 13
    let mutated = appliedMutable w 4
    mutated |> should equal 13
    let nested = appliedNested (CaptureEcho()) 4
    nested |> should equal "5|6|7"

/// Fields attributed by data flow first (#285): each traced back through the optimized tree to the
/// source variable it is, the rules only where it has no answer. The rules alone refused both.
type HeldInt = { HeldValue: int }
let inline private unholdInt (h: HeldInt) : int = h.HeldValue
type BoxedInt = BoxedInt of int
let inline private unboxInt (BoxedInt v) : int = v
let private (|Present|_|) (x: 'T) = Some x

/// After a `let rec` binding its own `x`, an alias of the outer `x` beside the inner one in a
/// second `let rec`: the alias's field traces to the outer `x` through `go`'s lifted parameter.
let private aliasAfterRecursive (o: obj) (seed: int) : string =
    let rec h (j: int) : int = if j > 0 then h (j - 1) else (let x = seed * 10 + 91 in x)
    ignore (h 1)
    let x = seed * 10 + 1
    let a = x
    let rec go (j: int) : string =
        if j > 0 then go (j - 1)
        else
            let x = seed * 10 + 2
            // fsharpanalyzer: ignore-line-next DLR001
            dlr { return o?Echo(a, unholdInt { HeldValue = x }) }
    go 1

/// An always-`Some` partial active pattern's `x` beside an alias of a mutable `x`: the rules gave the
/// pattern's field to the mutable `x` (by range), data flow to the match input it holds. They
/// disagree, so the field is left out, and the block binds by name, the one `x` it reads.
let private aliasBesidePattern (o: obj) (seed: int) : string =
    let mutable x = seed * 10 + 1
    x <- x + 5
    let a = x
    match seed * 10 + 2 with
    | Present x ->
        // fsharpanalyzer: ignore-line-next DLR001
        dlr { return o?Echo(a, unboxInt (BoxedInt x)) }
    | _ -> "none"

[<Fact>]
let ``fields attributed by data flow bind two variables of one name`` () =
    needsBodies ()
    let o: obj = CaptureEcho()
    let afterRecursive = aliasAfterRecursive o 4
    afterRecursive |> should equal "41|42"
    let besidePattern = aliasBesidePattern o 4
    besidePattern |> should equal "46|42"

/// A string constant UTF-8 cannot carry (a lone surrogate): the map would read back U+FFFD, a
/// different constant, so the member is left out of the map and needs the attribute.
let private loneSurrogate (w: obj) : int =
    let s = "\uD800"
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return w?Count + int s.[0] }

[<Fact>]
let ``a string constant UTF-8 cannot carry leaves the member out`` () =
    needsBodies ()
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> loneSurrogate (Widget()) |> ignore)
    ex.Message |> should haveSubstring "[<ReflectedDefinition>]"

let private loneSurrogateChar (w: obj) : int =
    let c = '\uDC00'
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return w?Count + int c }

[<Fact>]
let ``a char constant UTF-8 cannot carry leaves the member out`` () =
    needsBodies ()
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> loneSurrogateChar (Widget()) |> ignore)
    ex.Message |> should haveSubstring "[<ReflectedDefinition>]"

#if NET
/// Per copy of this assembly: two copies in two load contexts, sharing the library, each read
/// their own map, and its names as their own copy resolves them (#259's review: by simple name in
/// the AppDomain, the second copy's block read the first copy's value).
let mutable copyTag = 0

let private readCopy (w: obj) : int =
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return w?Count + copyTag }

type private CopyContext(path: string) =
    inherit System.Runtime.Loader.AssemblyLoadContext("Tests copy", true)
    override this.Load(name) = if name.Name = "Tests" then this.LoadFromAssemblyPath path else null

[<Fact>]
let ``two copies of the assembly in two load contexts each read their own`` () =
    needsBodies ()
    let path = typeof<Widget>.Assembly.Location
    let copy (tag: int) =
        let companion = CopyContext(path).LoadFromAssemblyPath(path).GetType "Tests.Companion"
        companion.GetProperty("copyTag").SetValue(null, tag)
        let read = companion.GetMethod("readCopy", System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Static)
        fun (w: obj) -> read.Invoke(null, [| w |]) :?> int
    let second, first = copy 20, copy 10
    let w: obj = Widget()
    let fromSecond = second w
    fromSecond |> should equal 23
    let fromFirst = first w
    fromFirst |> should equal 13
#endif

/// An or-pattern's arm: the map's body holds the block once per alternative, and its copies are
/// that block (#263), as from a reflected definition; the field pass takes the first copy's `x`,
/// as the run time takes the first copy.
let private eitherWay (w: obj) (k: int) : int =
    match (if k > 0 then Choice1Of2(k * 10) else Choice2Of2(k * 10)) with
    | Choice1Of2 x
    | Choice2Of2 x ->
        // fsharpanalyzer: ignore-line-next DLR001
        dlr { return w?Count + x }

[<Fact>]
let ``an or-pattern's arm`` () =
    needsBodies ()
    let w: obj = Widget()
    let first = eitherWay w 5
    first |> should equal 53
    let second = eitherWay w -2
    second |> should equal -17

/// Two blocks of one member on one line: refused as the reflected path refuses them, by the line,
/// not as a missing attribute.
let private twoOnOneLine (w: obj) : int * string =
    // fsharpanalyzer: ignore-region-start DLR001
    // fsharpanalyzer: ignore-line-next DLR003
    let a: int = dlr { return w?Count } in let b: string = dlr { return w?Name } in (a, b)
    // fsharpanalyzer: ignore-region-end DLR001

[<Fact>]
let ``two blocks of a member on one line are refused by the line`` () =
    needsBodies ()
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> twoOnOneLine (Widget()) |> ignore)
    ex.Message |> should haveSubstring "blocks share"

type IntToInt = System.Func<int, int>

let private viaAbbreviatedDelegate (w: obj) : int =
    let f = IntToInt(fun x -> x + 1)
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return w?Count + f.Invoke 1 }

[<Fact>]
let ``a delegate constructed through a type abbreviation`` () =
    needsBodies ()
    viaAbbreviatedDelegate (Widget()) |> should equal 5

// The analyzer reports this at build time (DLR003).
// fsharpanalyzer: ignore-region-start DLR001
// fsharpanalyzer: ignore-region-start DLR003
type private SharedLine(w: obj) =
    member _.A : int = (dlr { return w?Count }) member _.B : int = (dlr { return w?Count * 2 })
// fsharpanalyzer: ignore-region-end DLR003
// fsharpanalyzer: ignore-region-end DLR001

[<Fact>]
let ``two members' blocks on one line are refused, as from reflected definitions`` () =
    needsBodies ()
    let s = SharedLine(Widget())
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> s.A |> ignore)
    ex.Message |> should haveSubstring "share"

let private byteConstant (w: obj) : int =
    let b = "abc"B
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return w?Count + b.Length }

let private nativeConstant (w: obj) : int =
    let n = 5n
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return w?Count + int n }

[<Fact>]
let ``a constant the map cannot carry leaves the member out: the attribute is needed`` () =
    needsBodies ()
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> byteConstant (Widget()) |> ignore)
    ex.Message |> should haveSubstring "[<ReflectedDefinition>]"
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> nativeConstant (Widget()) |> ignore)
    ex.Message |> should haveSubstring "[<ReflectedDefinition>]"

let private fromDecimal (w: obj) (d: decimal) : int =
    let n = int d
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return w?Count + n }

[<Fact>]
let ``a conversion overloaded only by its return type`` () =
    needsBodies ()
    fromDecimal (Widget()) 4m |> should equal 7

type Widget with
    member w.Thrice = w.Count * 3

let private viaExtension (w: Widget) : int =
    let t = w.Thrice
    let o = box w
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return o?Count + t }

[<Fact>]
let ``an F# extension member`` () =
    needsBodies ()
    viaExtension (Widget()) |> should equal 12

let private rawQuotation (w: obj) : int =
    let q: FSharp.Quotations.Expr = <@@ 1 @@>
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return w?Count + (if isNull (box q) then 0 else 1) }

[<Fact>]
let ``a raw quotation literal`` () =
    needsBodies ()
    rawQuotation (Widget()) |> should equal 4

let mutable private sideEffects = 0
let private bumpSide () = sideEffects <- sideEffects + 1

/// With the attribute, so the oracle compares the map's body with it: a unit argument with an effect.
[<ReflectedDefinition>]
let private unitArgumentWithEffect (w: obj) : int =
    bumpSide (sideEffects <- sideEffects + 10)
    dlr { return w?Count }

[<Fact>]
let ``a unit argument with an effect keeps it`` () =
    unitArgumentWithEffect (Widget()) |> should equal 3

let private userNamedTupled (x_0: obj, x_1: int) : int =
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return x_0?Count + x_1 }

let private userNamedCurried (x_0: obj) (x_1: int) : int =
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return x_0?Count + x_1 }

let private realSplit (x: obj * int) : int =
    // fsharpanalyzer: ignore-line-next DLR001
    dlr { return (fst x)?Count + snd x }

[<Fact>]
let ``parameters named like a split tuple's elements are themselves, and a real split is one tuple`` () =
    needsBodies ()
    userNamedTupled (Widget(), 4) |> should equal 7
    userNamedCurried (Widget()) 4 |> should equal 7
    realSplit (Widget(), 4) |> should equal 7

/// With the attribute, so the oracle compares the map's body with it: or-patterns binding a variable,
/// whose target the quotation copies into each leaf that reaches it.
[<ReflectedDefinition>]
let private orPatterns (w: obj) (c: Choice<int, int>) (xs: int list) : int =
    let a = match c with Choice1Of2 x | Choice2Of2 x -> x
    let b = match xs with [ x ] | [ _; x ] -> x | _ -> 0
    dlr { return w?Count + a * 10 + b }

[<Fact>]
let ``or-patterns before a block`` () =
    orPatterns (Widget()) (Choice2Of2 4) [ 1; 2 ] |> should equal 45
