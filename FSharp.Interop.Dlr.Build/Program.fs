module FSharp.Interop.Dlr.Build.Program

open System
open System.IO

/// The build companion (#216): one FCS check of the project, its passes over it, one map.
///
///   FSharp.Interop.Dlr.Build <fsc args file, one per line> <project dir> <output map>
///
/// Map format 1 (tab-separated lines): `DLRMAP 1` first; `B file line` starts a block; `S section`
/// starts one of its optional sections, `fields` (Fields) and `body` (Bodies). The run time
/// (CaptureMap.fs) ignores a map of another version whole and skips a section it does not know.
[<EntryPoint>]
let main argv =
    match argv with
    | [| argsFile; projectDir; output |] ->
        let results = Check.project argsFile projectDir
        let fields = Fields.run results.AssemblyContents (results.GetOptimizedAssemblyContents())
        let bodies = Bodies.run results
        let keys = Seq.append fields.Keys bodies.Keys |> Seq.distinct |> Seq.sort |> List.ofSeq
        let lines =
            [ for (file, line) as key in keys do
                let sections =
                    [ match fields.TryGetValue key with
                      | true, Some entries -> yield "S\tfields" :: entries
                      | _ -> ()
                      match bodies.TryGetValue key with
                      | true, entries -> yield "S\tbody" :: entries
                      | _ -> () ]
                if not sections.IsEmpty then
                    yield sprintf "B\t%s\t%d" file line
                    yield! List.concat sections ]
        File.WriteAllLines(output, "DLRMAP\t1" :: lines)
        let mapped = fields.Values |> Seq.filter Option.isSome |> Seq.length
        eprintfn "%d blocks, %d with fields, %d with a body" fields.Count mapped bodies.Count
        0
    | _ ->
        eprintfn "usage: FSharp.Interop.Dlr.Build <fsc args file> <project dir> <output map>"
        2
