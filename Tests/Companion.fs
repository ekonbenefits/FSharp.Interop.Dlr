/// Blocks with no [<ReflectedDefinition>] anywhere: the build companion's map holds their members'
/// bodies (`FSharp.Interop.Dlr.Build/Bodies.fs`, academic, #219 on #216). Without a map embedded
/// (`-p:DlrCompanion=true`) they skip.
module Tests.Companion

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

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
    if not bodiesMapped then raise (AnyUnit.IgnoreException "no block bodies embedded (Companion/bodies.fsx)")

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
