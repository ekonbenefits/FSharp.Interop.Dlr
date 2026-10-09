namespace FSharp.Interop.Dlr

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Reflection

/// One field of a block's Release state machine and the source variable it holds: the
/// variable's name and its order among the member's bindings of that name, and for a split
/// tuple's element field, the element. `Unused`: no field, a split tuple's element the
/// compiler keeps nowhere.
type internal CaptureEntry = { Field: string; Name: string; Ordinal: int; Element: int option; Unused: bool }

/// The capture map a build companion embeds (`FSharp.Interop.Dlr.CaptureMap`), computed from the
/// compiler's optimized tree: which variable each field of each block's state machine holds, so
/// captures bind exactly instead of by name. Format 1, tab-separated lines: `DLRMAP 1` first;
/// `B file line` starts a block; `S section` one of its optional sections. `fields` holds
/// `V field name ordinal`, `E field name ordinal element` and `U name ordinal element` (a split
/// tuple's element nothing keeps); a later companion may add `body`, which this skips.
module internal CaptureMap =

    [<Literal>]
    let ResourceName = "FSharp.Interop.Dlr.CaptureMap"

    /// `DLR_CAPTURE_MAP_TRACE=1`: report per block whether its captures bound through the map.
    let trace = Environment.GetEnvironmentVariable "DLR_CAPTURE_MAP_TRACE" = "1"

    let private byAssembly = ConcurrentDictionary<Assembly, IReadOnlyDictionary<struct (string * int), CaptureEntry list>>()

    /// The format this loader reads (`DLRMAP 1`); a map of any other version, or none, is ignored
    /// whole, so every block keeps the strict behaviour.
    [<Literal>]
    let Version = "1"

    let private load (assembly: Assembly) : IReadOnlyDictionary<struct (string * int), CaptureEntry list> =
        let blocks = Dictionary<struct (string * int), CaptureEntry list>()
        match (try assembly.GetManifestResourceStream ResourceName with _ -> null) with
        | null -> ()
        | stream ->
            use reader = new StreamReader(stream)
            if reader.ReadLine() = "DLRMAP\t" + Version then
                // Per block, the sections are optional: a block with no `fields` section has no
                // entry here (strict); a section this loader does not know (a later `body`) is skipped.
                let mutable key = None
                let mutable section = ""
                let mutable entries = []
                let mutable hasFields = false
                let flush () =
                    match key with
                    | Some k when hasFields -> blocks.[k] <- List.rev entries
                    | _ -> ()
                let mutable line = reader.ReadLine()
                while not (isNull line) do
                    match line.Split '\t' with
                    | [| "B"; file; at |] ->
                        flush ()
                        key <- Some(struct (file, int at))
                        section <- ""
                        entries <- []
                        hasFields <- false
                    | [| "S"; name |] ->
                        section <- name
                        if name = "fields" then hasFields <- true
                    | fields when section = "fields" ->
                        match fields with
                        | [| "V"; field; name; ordinal |] -> entries <- { Field = field; Name = name; Ordinal = int ordinal; Element = None; Unused = false } :: entries
                        | [| "E"; field; name; ordinal; element |] ->
                            entries <- { Field = field; Name = name; Ordinal = int ordinal; Element = Some(int element); Unused = false } :: entries
                        // A split tuple's element nothing in the optimized member keeps.
                        | [| "U"; name; ordinal; element |] ->
                            entries <- { Field = ""; Name = name; Ordinal = int ordinal; Element = Some(int element); Unused = true } :: entries
                        | _ -> ()
                    | _ -> ()
                    line <- reader.ReadLine()
                flush ()
        blocks :> _

    /// The block's entries, when its assembly carries a map with one for its file and line.
    let find (assembly: Assembly) (file: string) (line: int) : CaptureEntry list option =
        match byAssembly.GetOrAdd(assembly, load).TryGetValue(struct (file, line)) with
        | true, entries -> Some entries
        | _ -> None
