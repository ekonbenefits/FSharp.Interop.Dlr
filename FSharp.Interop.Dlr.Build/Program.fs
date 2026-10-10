module FSharp.Interop.Dlr.Build.Program

open System
open System.IO
open System.IO.Compression
open System.Text

// The build companion (#216): one FCS check of the project, its passes over it, one map.
//
//   FSharp.Interop.Dlr.Build [--compiler <fsc.dll>] <fsc args file, one per line> <project dir> <output map>
//
// Map format 2 (CaptureMap.fs reads it): a header of tab-separated lines, `DLRMAP 2`, an
// `F offset length file` line per source file, an empty line; then each file's blob,
// raw-deflated: its blocks as lines, `B file line` and its optional sections `S fields` (Fields)
// and `S body` (Bodies). Compressed per file, where the names that repeat across members are.

/// The companion's work, against whichever FCS this assembly was loaded with (see the entry point).
/// Public and in a module so the entry point can call it in another load context by reflection.
module Run =

    /// Writes `output`: format 2's header and, per source file, its blocks' lines deflated.
    let write (output: string) (perFile: (string * string list) list) =
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

    let run (argsFile: string) (projectDir: string) (output: string) : int =
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
        write output perFile
        let mapped = fields.Values |> Seq.filter Option.isSome |> Seq.length
        eprintfn "%d blocks, %d with fields, %d with a body" fields.Count mapped bodies.Count
        eprintfn "FCS: %s" (typeof<FSharp.Compiler.CodeAnalysis.FSharpChecker>.Assembly.Location)
        0

/// Loads FCS and FSharp.Core from the compiler's own directory (the one holding the fsc.dll that
/// builds the project), and this tool's assemblies from beside it: the map then describes what
/// that compiler emits, not what the FCS this tool was built against would.
type private CompilerContext(compilerDir: string, toolDir: string) =
    inherit System.Runtime.Loader.AssemblyLoadContext("dlr-companion-compiler", false)
    override this.Load(name: System.Reflection.AssemblyName) =
        let at (dir: string) = Path.Combine(dir, name.Name + ".dll")
        if File.Exists(at compilerDir) then this.LoadFromAssemblyPath(at compilerDir)
        elif File.Exists(at toolDir) then this.LoadFromAssemblyPath(at toolDir)
        else null

/// `--compiler <fsc.dll>`: run against that compiler's FCS. Any failure there (an FCS whose API
/// moved, say) writes an empty map instead, so every block keeps the strict behaviour and the
/// build goes on: the companion can turn itself off, never bind a capture wrongly.
[<EntryPoint>]
let main argv =
    match argv with
    | [| "--compiler"; fsc; argsFile; projectDir; output |] ->
        let compilerDir = Path.GetDirectoryName(Path.GetFullPath(fsc.Trim('"')))
        let self = System.Reflection.Assembly.GetExecutingAssembly()
        try
            if not (File.Exists(Path.Combine(compilerDir, "FSharp.Compiler.Service.dll"))) then
                failwithf "no FSharp.Compiler.Service.dll beside %s" fsc
            let context = CompilerContext(compilerDir, Path.GetDirectoryName self.Location)
            let loaded = context.LoadFromAssemblyPath self.Location
            let run = loaded.GetType("FSharp.Interop.Dlr.Build.Program+Run").GetMethod("run")
            run.Invoke(null, [| box argsFile; box projectDir; box output |]) :?> int
        with e ->
            let e = match e with :? System.Reflection.TargetInvocationException as t when not (isNull t.InnerException) -> t.InnerException | e -> e
            eprintfn "dlr companion: the capture map is off for this build (%s: %s); every block keeps the strict behaviour" (e.GetType().Name) e.Message
            Run.write output []
            0
    | [| argsFile; projectDir; output |] -> Run.run argsFile projectDir output
    | _ ->
        eprintfn "usage: FSharp.Interop.Dlr.Build [--compiler <fsc.dll>] <fsc args file> <project dir> <output map>"
        2
