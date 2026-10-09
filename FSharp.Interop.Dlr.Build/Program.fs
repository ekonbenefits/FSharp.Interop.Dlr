module FSharp.Interop.Dlr.Build.Program

open System
open System.IO
open System.IO.Compression
open System.Text

/// The build companion (#216): one FCS check of the project, its passes over it, one map.
///
///   FSharp.Interop.Dlr.Build <fsc args file, one per line> <project dir> <output map>
///
/// Map format 2 (CaptureMap.fs reads it): a header of tab-separated lines, `DLRMAP 2`, an
/// `F offset length file` line per source file, an empty line; then each file's blob,
/// raw-deflated: its blocks as lines, `B file line` and its optional sections `S fields` (Fields)
/// and `S body` (Bodies). Compressed per file, where the names that repeat across members are.
[<EntryPoint>]
let main argv =
    match argv with
    | [| argsFile; projectDir; output |] ->
        let results = Check.project argsFile projectDir
        let fields = Fields.run results.AssemblyContents (results.GetOptimizedAssemblyContents())
        let bodies = Bodies.run results
        let keys = Seq.append fields.Keys bodies.Keys |> Seq.distinct |> Seq.sort |> List.ofSeq
        // Per source file, its blocks' lines: `B`, then whichever sections the block has.
        let perFile =
            [ for file, fileKeys in keys |> List.groupBy fst ->
                file,
                [ for (_, line) as key in fileKeys do
                    let sections =
                        [ match fields.TryGetValue key with
                          | true, Some entries -> yield "S\tfields" :: entries
                          | _ -> ()
                          match bodies.TryGetValue key with
                          | true, entries -> yield "S\tbody" :: entries
                          | _ -> () ]
                    if not sections.IsEmpty then
                        yield sprintf "B\t%s\t%d" file line
                        yield! List.concat sections ] ]
            |> List.filter (fun (_, lines) -> not lines.IsEmpty)
        // Format 2: the header (`DLRMAP 2`, an `F offset length file` line per file, an empty line),
        // then each file's lines raw-deflated, at its offset from the header's end.
        let blobs =
            [ for file, lines in perFile ->
                use buffer = new MemoryStream()
                (use deflate = new DeflateStream(buffer, CompressionLevel.Optimal, true)
                 let text = Encoding.UTF8.GetBytes(String.concat "\n" lines + "\n")
                 deflate.Write(text, 0, text.Length))
                file, buffer.ToArray() ]
        let header =
            let mutable offset = 0
            [ yield "DLRMAP\t2"
              for file, blob in blobs do
                  yield sprintf "F\t%d\t%d\t%s" offset blob.Length file
                  offset <- offset + blob.Length ]
        use out = File.Create output
        let head = Encoding.UTF8.GetBytes(String.concat "\n" header + "\n\n")
        out.Write(head, 0, head.Length)
        for _, blob in blobs do out.Write(blob, 0, blob.Length)
        let mapped = fields.Values |> Seq.filter Option.isSome |> Seq.length
        eprintfn "%d blocks, %d with fields, %d with a body" fields.Count mapped bodies.Count
        0
    | _ ->
        eprintfn "usage: FSharp.Interop.Dlr.Build <fsc args file> <project dir> <output map>"
        2
