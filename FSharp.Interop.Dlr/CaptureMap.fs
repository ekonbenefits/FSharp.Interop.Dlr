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
/// captures bind exactly instead of by name. Tab-separated lines: `B file line` starts a block;
/// `V field name ordinal` and `E field name ordinal element` follow, and `U name ordinal element`
/// for a split tuple's element nothing keeps.
module internal CaptureMap =

    [<Literal>]
    let ResourceName = "FSharp.Interop.Dlr.CaptureMap"

    let private byAssembly = ConcurrentDictionary<Assembly, IReadOnlyDictionary<struct (string * int), CaptureEntry list>>()

    let private load (assembly: Assembly) : IReadOnlyDictionary<struct (string * int), CaptureEntry list> =
        let blocks = Dictionary<struct (string * int), CaptureEntry list>()
        match (try assembly.GetManifestResourceStream ResourceName with _ -> null) with
        | null -> ()
        | stream ->
            use reader = new StreamReader(stream)
            let mutable key = None
            let mutable entries = []
            let flush () =
                match key with
                | Some k -> blocks.[k] <- List.rev entries
                | None -> ()
            let mutable line = reader.ReadLine()
            while not (isNull line) do
                match line.Split '\t' with
                | [| "B"; file; at |] ->
                    flush ()
                    key <- Some(struct (file, int at))
                    entries <- []
                | [| "V"; field; name; ordinal |] -> entries <- { Field = field; Name = name; Ordinal = int ordinal; Element = None; Unused = false } :: entries
                | [| "E"; field; name; ordinal; element |] ->
                    entries <- { Field = field; Name = name; Ordinal = int ordinal; Element = Some(int element); Unused = false } :: entries
                // A split tuple's element nothing in the optimized member keeps.
                | [| "U"; name; ordinal; element |] ->
                    entries <- { Field = ""; Name = name; Ordinal = int ordinal; Element = Some(int element); Unused = true } :: entries
                | _ -> ()
                line <- reader.ReadLine()
            flush ()
        blocks :> _

    /// The block's entries, when its assembly carries a map with one for its file and line.
    let find (assembly: Assembly) (file: string) (line: int) : CaptureEntry list option =
        match byAssembly.GetOrAdd(assembly, load).TryGetValue(struct (file, line)) with
        | true, entries -> Some entries
        | _ -> None
