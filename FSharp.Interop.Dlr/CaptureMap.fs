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

/// The capture map a build companion embeds (`FSharp.Interop.Dlr.CaptureMap`, written by
/// FSharp.Interop.Dlr.Build): per dlr { } block, which variable each field of its state machine
/// holds (`fields`, so captures bind exactly instead of by name) and the body of its member
/// (`body`, so a block needs no [<ReflectedDefinition>]). This is the one place the resource is
/// read; `find` serves the fields, `body` a block's raw body lines to BodyMap.
///
/// Format 2, one resource: an uncompressed header of tab-separated lines, `DLRMAP 2`, then
/// `F offset length file` per source file, then an empty line; after it, each file's blob,
/// raw-deflated, at its offset from the header's end. A blob holds that file's blocks as lines:
/// `B file line` starts a block; `S section` one of its optional sections; `fields` holds
/// `V field name ordinal`, `E field name ordinal element` and `U name ordinal element` (a split
/// tuple's element nothing keeps); `body` lines are BodyMap's. A file's blob is inflated the first
/// time one of its blocks is looked up. A map of another version, or a malformed one, is ignored
/// whole, and a block without a section has none: every such block keeps the strict behaviour,
/// or for a body, needs the attribute.
module internal CaptureMap =

    [<Literal>]
    let ResourceName = "FSharp.Interop.Dlr.CaptureMap"

    /// `DLR_CAPTURE_MAP_TRACE=1`: report per block whether its captures bound through the map.
    let trace = Environment.GetEnvironmentVariable "DLR_CAPTURE_MAP_TRACE" = "1"

    /// The format this loader reads (`DLRMAP 2`).
    [<Literal>]
    let Version = "2"

    /// A block's sections: its fields when it has a `fields` section, and its `body` lines.
    type private Block = { Fields: CaptureEntry list option; Body: string list option }

    /// An assembly's map: per source file, its blob (offset, length) and, once a block of it was
    /// looked up, its blocks.
    type private Resource =
        { Bytes: byte[]
          Start: int
          Files: IReadOnlyDictionary<string, struct (int * int)>
          Parsed: ConcurrentDictionary<string, IReadOnlyDictionary<int, Block>> }

    let private byAssembly = ConcurrentDictionary<Assembly, Resource option>()

    /// The header: the version line, the `F` lines, the empty line. None when it is not format 2.
    let private load (assembly: Assembly) : Resource option =
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
                    let mutable ok = true
                    for l in Seq.skip 1 lines do
                        match l.Split '\t' with
                        | [| "F"; offset; length; file |] ->
                            let o, n = int offset, int length
                            if o < 0 || n < 0 || start + o + n > bytes.Length then ok <- false
                            else files.[file] <- struct (o, n)
                        | _ -> ok <- false
                    if ok then Some { Bytes = bytes; Start = start; Files = files; Parsed = ConcurrentDictionary() } else None

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
            let mutable fields = None
            let mutable body = None
            let flush () =
                match key with
                | Some k -> blocks.[k] <- { Fields = fields |> Option.map List.rev; Body = body |> Option.map List.rev }
                | None -> ()
            let mutable line = reader.ReadLine()
            while not (isNull line) do
                match section, line.Split '\t' with
                | _, [| "B"; _; at |] ->
                    flush ()
                    key <- Some(int at)
                    section <- ""
                    fields <- None
                    body <- None
                | _, [| "S"; name |] ->
                    section <- name
                    if name = "fields" then fields <- Some []
                    elif name = "body" then body <- Some []
                | "fields", parts ->
                    let entry =
                        match parts with
                        | [| "V"; field; name; ordinal |] -> Some { Field = field; Name = name; Ordinal = int ordinal; Element = None; Unused = false }
                        | [| "E"; field; name; ordinal; element |] -> Some { Field = field; Name = name; Ordinal = int ordinal; Element = Some(int element); Unused = false }
                        // A split tuple's element nothing in the optimized member keeps.
                        | [| "U"; name; ordinal; element |] -> Some { Field = ""; Name = name; Ordinal = int ordinal; Element = Some(int element); Unused = true }
                        | _ -> None
                    entry |> Option.iter (fun e -> fields <- fields |> Option.map (fun es -> e :: es))
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
            match map.Parsed.GetOrAdd(file, parse map).TryGetValue line with
            | true, b -> Some b
            | _ -> None
        | _ -> None

    /// The block's field entries, when its assembly's map has a `fields` section for its file and line.
    let find (assembly: Assembly) (file: string) (line: int) : CaptureEntry list option =
        block assembly file line |> Option.bind (fun b -> b.Fields)

    /// The block's `body` lines as the companion wrote them (BodyMap decodes them), when its
    /// assembly's map has a `body` section for its file and line.
    let body (assembly: Assembly) (file: string) (line: int) : string list option =
        block assembly file line |> Option.bind (fun b -> b.Body)
