// Checks that what docs/*.md (and the README) name in backticks still exists (#161):
//   - an identifier (`DelegateMembers.standIn`, `FunctionBuilder`, `DelegateLiteral<'D>.Over`)
//     has every dotted segment either in the repository's sources or among the types and
//     members of the framework and FSharp.Core, and a member of one of our types (`A.B`, `A`
//     ours) is in `A`'s own declaration;
//   - a repository path (`Tests/HotPath.fs`, `generate-adapters.fsx`) exists.
// Snippets (anything with spaces, operators or `?`) are not checked. Exit code 1 on any miss.
//   dotnet fsi docs/check-identifiers.fsx
open System
open System.IO
open System.Text.RegularExpressions

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let skipped (path: string) =
    let parts = path.Substring(root.Length).Split([| '/'; '\\' |])
    parts |> Array.exists (fun p -> p = "bin" || p = "obj" || p = ".claude" || p = ".git" || p = "node_modules" || p = "TestResults")

let files (patterns: string list) =
    [ for p in patterns do yield! Directory.EnumerateFiles(root, p, SearchOption.AllDirectories) ]
    |> List.filter (skipped >> not)

// Every word in the sources.
let ours =
    files [ "*.fs"; "*.fsi"; "*.fsx"; "*.cs"; "*.fsproj"; "*.csproj"; "*.props"; "*.yml"; "*.sh"; "*.mjs"; "*.json" ]
    |> List.filter (fun f -> Path.GetFullPath f <> Path.GetFullPath __SOURCE_FILE__ && Path.GetFileName f <> "check-identifiers.fsx")   // its own examples are not sources
    |> List.collect (fun f -> [ for m in Regex.Matches(File.ReadAllText f, @"[A-Za-z_][\w']*") -> m.Value ])
    |> set

// Our types and modules, each with the words of its declaration's body (indented below it), so
// `A.B` with `A` ours requires `B` inside `A`, not just anywhere (`CurriedInvoker.build` when
// only `FunctionBuilder.build` exists).
let owners =
    // `type`/`module`, and `and` before an upper-case name (a mutually recursive type; an `and`
    // function binding is lower-case).
    let decl = Regex(@"^(\s*)(?:\[<[^>]*>\]\s*)?(?:(?:type|module)\s+(?:(?:internal|private|public|rec)\s+)*([A-Za-z_][\w']*)|and\s+(?:\[<[^>]*>\]\s*)?(?:(?:internal|private|public)\s+)*([A-Z][\w']*))")
    let indentOf (l: string) = l.Length - l.TrimStart().Length
    [ for f in files [ "*.fs"; "*.fsi" ] do
        let lines = File.ReadAllLines f
        for i, line in Array.indexed lines do
            let m = decl.Match line
            if m.Success then
                let indent = m.Groups.[1].Value.Length
                let body =
                    lines
                    |> Seq.skip (i + 1)
                    |> Seq.takeWhile (fun l -> l.Trim() = "" || indentOf l > indent || l.TrimStart().StartsWith "//" || l.TrimStart().StartsWith "[<")
                    |> String.concat "\n"
                yield (if m.Groups.[2].Success then m.Groups.[2].Value else m.Groups.[3].Value), set [ for w in Regex.Matches(line + "\n" + body, @"[A-Za-z_][\w']*") -> w.Value ] ]
    |> List.groupBy fst
    |> List.map (fun (name, bodies) -> name, Set.unionMany (List.map snd bodies))
    |> Map.ofList

// Every type and member name of the framework and FSharp.Core.
let framework =
    let dir = Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
    let assemblies =
        [ yield typeof<option<int>>.Assembly
          yield typeof<obj>.Assembly
          for dll in Directory.EnumerateFiles(dir, "*.dll") do
              match (try Some(Reflection.Assembly.LoadFrom dll) with _ -> None) with
              | Some a -> yield a
              | None -> () ]
    [ for a in assemblies do
        let types = try a.GetTypes() with :? Reflection.ReflectionTypeLoadException as e -> e.Types |> Array.filter (isNull >> not)
        for t in types do
            yield t.Name.Split('`').[0]
            for m in (try t.GetMembers() with _ -> [||]) do
                yield m.Name
                // FSharp.Core's functions compile to capitalised names (`List.map` is `Map`).
                if a = typeof<option<int>>.Assembly && m.Name.Length > 0 then
                    yield (string (Char.ToLowerInvariant m.Name.[0]) + m.Name.Substring 1) ]
    |> set

// F# keywords and type abbreviations a doc may name in backticks.
let fsharp =
    set [ "let"; "rec"; "mutable"; "use"; "fun"; "function"; "match"; "with"; "try"; "finally"; "if"; "then"; "else"; "for"
          "while"; "do"; "return"; "yield"; "in"; "of"; "type"; "module"; "member"; "static"; "inline"; "internal"; "private"
          "public"; "null"; "true"; "false"; "not"; "and"; "or"; "new"; "lazy"; "async"; "task"; "seq"; "unit"; "int"; "int64"
          "int16"; "uint"; "uint32"; "uint64"; "byte"; "sbyte"; "float"; "float32"; "single"; "double"; "decimal"; "string"
          "char"; "bool"; "obj"; "exn"; "list"; "option"; "voption"; "array"; "byref"; "outref"; "inref"; "nativeint"
          "struct"; "void"; "dynamic"; "ref"; "out"; "params"; "this"; "base"; "sizeof"; "typeof"; "nameof"; "reraise"
          "raise"; "failwith"; "ignore"; "box"; "unbox"; "id"; "dlr"; "fsi"; "fsc" ]

// Placeholder names a doc uses for illustration.
let placeholders = set [ "Foo"; "Bar"; "Baz"; "IFoo" ]

/// `exact` of the segment, or of a family `CurriedN` standing for `Curried0`, `Curried1`, ….
let orFamily (exact: string -> bool) (segment: string) =
    exact segment
    || (segment.Length > 1 && segment.EndsWith "N"
        && (let prefix = segment.Substring(0, segment.Length - 1) in [ 0 .. 16 ] |> List.exists (fun n -> exact (prefix + string n))))

/// A segment exists: in the sources, the framework, F#'s vocabulary, the doc's own code blocks
/// (a diagram's node names), or as a family.
let known (local: Set<string>) =
    orFamily (fun s -> ours.Contains s || framework.Contains s || fsharp.Contains s || placeholders.Contains s || local.Contains s)

/// The sources as one text, for a dotted name written whole (a project or namespace).
let ourText = files [ "*.fs"; "*.fsi"; "*.fsproj"; "*.slnx"; "*.props" ] |> List.map File.ReadAllText |> String.concat "\n"

/// `A<'T, B<C>>.M` → `A.M`: generic arguments are types already named elsewhere, or type variables.
let rec stripGenerics (s: string) =
    let s' = Regex.Replace(s, @"<[^<>]*>", "")
    if s' = s then s else stripGenerics s'

let identifier = Regex(@"^[A-Za-z_][\w']*(\.[A-Za-z_][\w']*)*$")
let pathLike = Regex(@"^[\w.\-/]+\.(fs|fsi|fsx|cs|md|sh|mjs|yml|json|props|fsproj|csproj|slnx)$")

let docs = [ yield! Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md"); yield Path.Combine(root, "README.md") ]
let misses =
    [ for doc in docs do
        let lines = File.ReadAllLines doc
        // Words in the doc's own mermaid diagrams: node names the prose refers to. Not code
        // samples, which could otherwise vouch for a removed name the prose repeats.
        let local =
            let text = File.ReadAllText doc
            [ for b in Regex.Matches(text, @"```mermaid[\s\S]*?```") do for w in Regex.Matches(b.Value, @"[A-Za-z_][\w']*") -> w.Value ] |> set
        let mutable fenced = false
        for i, line in Array.indexed lines do
            if line.TrimStart().StartsWith "```" then fenced <- not fenced
            elif not fenced then
                for m in Regex.Matches(line, @"`([^`]+)`") do
                    let span = m.Groups.[1].Value.Trim()
                    let relative = Path.GetRelativePath(root, doc)
                    if pathLike.IsMatch span then
                        // A path from the repository root, from the doc's folder, or a bare file name anywhere.
                        let exists =
                            File.Exists(Path.Combine(root, span)) || File.Exists(Path.Combine(Path.GetDirectoryName doc, span))
                            || (not (span.Contains "/") && not (List.isEmpty (files [ span ])))
                        if not exists then yield sprintf "%s:%d: no file `%s`" relative (i + 1) span
                    else
                        let bare = (stripGenerics span).TrimEnd('(', ')')
                        if identifier.IsMatch bare then
                            let segments = bare.Split('.')
                            let unknown = segments |> Array.filter (known local >> not)
                            // `A.B` where `A` is ours: `B` belongs inside `A`'s declaration.
                            let misplaced =
                                // Written whole in the sources (a namespace, a project), as a whole name.
                                if Regex.IsMatch(ourText, @"(?<![\w.])" + Regex.Escape bare + @"(?![\w'])") then [||]
                                else
                                    segments
                                    |> Array.pairwise
                                    |> Array.choose (fun (a, b) ->
                                        match owners.TryFind a with
                                        | Some body when not (orFamily body.Contains b) -> Some(a + "." + b)
                                        | _ -> None)
                            if unknown.Length > 0 then
                                yield sprintf "%s:%d: `%s` — not found: %s" relative (i + 1) span (String.Join(", ", unknown))
                            elif misplaced.Length > 0 then
                                yield sprintf "%s:%d: `%s` — not a member: %s" relative (i + 1) span (String.Join(", ", misplaced)) ]

if misses.IsEmpty then printfn "docs: every named identifier and path exists (%d docs)" docs.Length
else
    misses |> List.iter (printfn "%s")
    printfn "%d name(s) in the docs no longer found in the sources or the framework" misses.Length
    exit 1
