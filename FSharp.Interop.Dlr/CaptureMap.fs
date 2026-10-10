namespace FSharp.Interop.Dlr

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Reflection
open System.Text

/// One field of a block's Release state machine and the source variable it holds: the
/// variable's name and its order among the member's bindings of that name, and for a split
/// tuple's element field, the element. `Unused`: no field, a split tuple's element the
/// compiler keeps nowhere.
type internal CaptureEntry = { Field: string; Name: string; Ordinal: int; Element: int option; Unused: bool }

/// A block's `fields` section: its entries, and per name they use, how many bindings of that name
/// the companion counted in the member (the run time refuses the block if the quotation has
/// another number: the companion and the quotation laid the member out differently).
type internal CaptureFields = { Entries: CaptureEntry list; Counts: Map<string, int> }

/// The capture map a build companion embeds (`FSharp.Interop.Dlr.CaptureMap`, written by
/// FSharp.Interop.Dlr.Build): per dlr { } block, which variable each field of its state machine
/// holds (`fields`, so captures bind exactly instead of by name) and the body of its member
/// (`body`, so a block needs no [<ReflectedDefinition>]). This is the one place the resource is
/// read; `find` serves the fields, `body` a block's raw body lines to BodyMap.
///
/// Format 2, one resource: an uncompressed header of tab-separated lines, `DLRMAP 2`, then
/// `F offset length file` per source file, then an empty line; after it, each file's blob,
/// raw-deflated, at its offset from the header's end. A blob holds that file's blocks as lines:
/// `B file line` starts a block (blobs are per file, so its file is the blob's); `S section` one of
/// its optional sections; `fields` holds `V field name ordinal`, `E field name ordinal element`,
/// `U name ordinal element` (a split tuple's element nothing keeps) and `N name count`; `body`
/// lines are BodyMap's. A file's blob is inflated the first time one of its blocks is looked up.
///
/// Anything malformed is never an exception: a bad header or `F` line, a blob that does not
/// inflate, and the map is ignored whole; a bad line inside a block's `fields`, and that block has
/// no fields. Such a block keeps the strict behaviour, or for a body, needs the attribute.
module internal CaptureMap =

    [<Literal>]
    let ResourceName = "FSharp.Interop.Dlr.CaptureMap"

    /// `DLR_CAPTURE_MAP_TRACE=1`: report per block whether its captures bound through the map.
    let trace = Environment.GetEnvironmentVariable "DLR_CAPTURE_MAP_TRACE" = "1"

    /// The format this loader reads (`DLRMAP 2`).
    [<Literal>]
    let Version = "2"

    /// A block's sections: its fields when it has a well-formed `fields` section, and its `body` lines.
    type private Block = { Fields: CaptureFields option; Body: string list option }

    /// An assembly's map: per source file, its blob (offset, length) and, once a block of it was
    /// looked up, its blocks.
    type private Resource =
        { Bytes: byte[]
          Start: int
          Files: IReadOnlyDictionary<string, struct (int * int)>
          Parsed: ConcurrentDictionary<string, IReadOnlyDictionary<int, Block>> }

    let private byAssembly = ConcurrentDictionary<Assembly, Resource option>()

    let private natural (text: string) =
        match Int32.TryParse(text, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
        | true, n -> Some n
        | _ -> None

    /// The header: the version line, the `F` lines, the empty line. None when it is not format 2,
    /// or any of it is malformed.
    let private read (assembly: Assembly) : Resource option =
        match (try assembly.GetManifestResourceStream ResourceName with _ -> null) with
        | null -> None
        | stream ->
            use stream = stream
            use memory = new MemoryStream()
            stream.CopyTo memory
            let bytes = memory.ToArray()
            // The header is the text up to the first empty line.
            let rec headerEnd i = if i + 1 >= bytes.Length then -1 elif bytes.[i] = 10uy && bytes.[i + 1] = 10uy then i + 2 else headerEnd (i + 1)
            match headerEnd 0 with
            | -1 -> None
            | start ->
                let lines = Encoding.UTF8.GetString(bytes, 0, start - 2).Split '\n'
                if lines.[0] <> "DLRMAP\t" + Version then None
                else
                    let files = Dictionary<string, struct (int * int)>()
                    let available = int64 bytes.Length - int64 start
                    let wellFormed =
                        Seq.skip 1 lines |> Seq.forall (fun l ->
                            match l.Split '\t' with
                            | [| "F"; offset; length; file |] ->
                                match natural offset, natural length with
                                | Some o, Some n when int64 o + int64 n <= available && not (files.ContainsKey file) ->
                                    files.[file] <- struct (o, n)
                                    true
                                | _ -> false
                            | _ -> false)
                    if wellFormed then Some { Bytes = bytes; Start = start; Files = files; Parsed = ConcurrentDictionary() } else None

    let private load (assembly: Assembly) : Resource option = try read assembly with _ -> None

    /// A `fields` line, or None when it is malformed.
    let private fieldLine (parts: string[]) : Choice<CaptureEntry, string * int> option =
        match parts with
        | [| "V"; field; name; ordinal |] ->
            natural ordinal |> Option.map (fun n -> Choice1Of2 { Field = field; Name = name; Ordinal = n; Element = None; Unused = false })
        | [| "E"; field; name; ordinal; element |] ->
            match natural ordinal, natural element with
            | Some n, Some i -> Some(Choice1Of2 { Field = field; Name = name; Ordinal = n; Element = Some i; Unused = false })
            | _ -> None
        // A split tuple's element nothing in the optimized member keeps.
        | [| "U"; name; ordinal; element |] ->
            match natural ordinal, natural element with
            | Some n, Some i -> Some(Choice1Of2 { Field = ""; Name = name; Ordinal = n; Element = Some i; Unused = true })
            | _ -> None
        | [| "N"; name; count |] -> natural count |> Option.map (fun c -> Choice2Of2(name, c))
        | _ -> None

    /// A file's blocks, by line, from its inflated blob.
    let private parse (map: Resource) (file: string) : IReadOnlyDictionary<int, Block> =
        let blocks = Dictionary<int, Block>()
        match map.Files.TryGetValue file with
        | true, struct (offset, length) ->
            use deflated = new MemoryStream(map.Bytes, map.Start + offset, length)
            use inflate = new DeflateStream(deflated, CompressionMode.Decompress)
            use reader = new StreamReader(inflate, Encoding.UTF8)
            let mutable key = None
            let mutable section = ""
            // A block's fields: None while there is no section, or once a line of it is malformed.
            let mutable fields: (CaptureEntry list * Map<string, int>) option = None
            let mutable broken = false
            let mutable body = None
            let flush () =
                match key with
                | Some k ->
                    let fields = if broken then None else fields |> Option.map (fun (es, counts) -> { Entries = List.rev es; Counts = counts })
                    blocks.[k] <- { Fields = fields; Body = body |> Option.map List.rev }
                | None -> ()
            let mutable line = reader.ReadLine()
            while not (isNull line) do
                match section, line.Split '\t' with
                | _, [| "B"; _; at |] ->
                    flush ()
                    key <- natural at
                    section <- ""
                    fields <- None
                    broken <- false
                    body <- None
                | _, [| "S"; name |] ->
                    section <- name
                    if name = "fields" then fields <- Some([], Map.empty)
                    elif name = "body" then body <- Some []
                | "fields", parts ->
                    match fieldLine parts, fields with
                    | Some(Choice1Of2 e), Some(es, counts) -> fields <- Some(e :: es, counts)
                    | Some(Choice2Of2(name, c)), Some(es, counts) -> fields <- Some(es, Map.add name c counts)
                    | _ -> broken <- true
                // A body line is BodyMap's, kept as written (its payload escaping included).
                | "body", _ -> body <- body |> Option.map (fun ls -> line :: ls)
                | _ -> ()
                line <- reader.ReadLine()
            flush ()
        | _ -> ()
        blocks :> _

    let private block (assembly: Assembly) (file: string) (line: int) : Block option =
        match byAssembly.GetOrAdd(assembly, load) with
        | Some map when map.Files.ContainsKey file ->
            // A blob that does not inflate is no map for that file.
            let blocks = map.Parsed.GetOrAdd(file, fun f -> try parse map f with _ -> (Dictionary<int, Block>() :> IReadOnlyDictionary<_, _>))
            match blocks.TryGetValue line with
            | true, b -> Some b
            | _ -> None
        | _ -> None

    /// The block's fields, when its assembly's map has a well-formed `fields` section for its file and line.
    let find (assembly: Assembly) (file: string) (line: int) : CaptureFields option =
        block assembly file line |> Option.bind (fun b -> b.Fields)

    /// The block's `body` lines as the companion wrote them (BodyMap decodes them), when its
    /// assembly's map has a `body` section for its file and line.
    let body (assembly: Assembly) (file: string) (line: int) : string list option =
        block assembly file line |> Option.bind (fun b -> b.Body)
