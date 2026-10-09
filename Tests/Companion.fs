/// Blocks with no [<ReflectedDefinition>] anywhere: the build companion's map holds their members'
/// bodies (`Companion/bodies.fsx`, academic, a follow-up to #216). Without a map embedded they skip.
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
