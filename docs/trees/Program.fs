/// Renders each example's compiled tree into the docs between `<!-- tree:name -->` markers (see
/// Trees.fsproj). With `check`, writes nothing and fails if any marker's content is stale.
module Trees.Program

open System
open System.IO
open System.Dynamic
open System.Linq.Expressions
open System.Runtime.CompilerServices
open System.Reflection
open System.Text.RegularExpressions
open AgileObjects.ReadableExpressions
open FSharp.Interop.Dlr

/// The repository root: the nearest ancestor of the binary holding the solution file. Not
/// __SOURCE_DIRECTORY__, which a CI build (ContinuousIntegrationBuild) maps to `/_/`.
let private root =
    let rec up (d: DirectoryInfo) =
        if isNull d then failwith "repository root (FSharp.Interop.Dlr.slnx) not found above the binary"
        elif File.Exists(Path.Combine(d.FullName, "FSharp.Interop.Dlr.slnx")) then d.FullName
        else up d.Parent
    up (DirectoryInfo AppContext.BaseDirectory)
let private pages = [ "docs/trees.md"; "docs/call-sites.md"; "docs/translation.md" ]

/// Each example's source: the lines between `// example: <name>` and `// end` in Examples.fs.
let private sources =
    let lines = File.ReadAllLines(Path.Combine(root, "docs", "trees", "Examples.fs"))
    [ for i, line in Array.indexed lines do
        if line.StartsWith "// example: " then
            let body = lines |> Seq.skip (i + 1) |> Seq.takeWhile (fun l -> l <> "// end") |> String.concat "\n"
            yield line.Substring 12, body ]
    |> Map.ofList

/// What a site does, from its binder: the operation, its member and arguments, and whose binder
/// binds it (ours, wrapping C#'s with the F# rules, or C#'s own).
let rec private describe (binder: CallSiteBinder) =
    let arguments (info: CallInfo) =
        match info.ArgumentCount, List.ofSeq info.ArgumentNames with
        | 0, _ -> ""
        | 1, [] -> ", 1 argument"
        | n, [] -> sprintf ", %d arguments" n
        | n, names -> sprintf ", %d arguments (named %s)" n (String.Join(", ", names))
    let operation =
        match binder with
        | :? MetaObjectAwareBinder as m -> describe m.Inner + ", meta-object aware"
        | :? ConvertBinder as c -> sprintf "Convert to %s (%s)" c.Type.Name (if c.Explicit then "explicit" else "implicit")
        | :? GetMemberBinder as g -> sprintf "GetMember %s" g.Name
        | :? SetMemberBinder as s -> sprintf "SetMember %s" s.Name
        | :? InvokeMemberBinder as i -> sprintf "InvokeMember %s%s" i.Name (arguments i.CallInfo)
        | :? InvokeBinder as i -> "Invoke" + arguments i.CallInfo
        | :? GetIndexBinder as g -> "GetIndex" + arguments g.CallInfo
        | :? SetIndexBinder as s -> "SetIndex" + arguments s.CallInfo
        | :? BinaryOperationBinder as b -> sprintf "BinaryOperation %O" b.Operation
        | :? UnaryOperationBinder as u -> sprintf "UnaryOperation %O" u.Operation
        | b -> b.GetType().Name.Replace("CSharp", "").Replace("FSharp", "").Replace("Binder", "")
    match binder with
    | :? MetaObjectAwareBinder -> operation
    | b when b.GetType().Namespace = "FSharp.Interop.Dlr" -> sprintf "%s: %s (C#'s, plus the F# rules)" operation (b.GetType().Name)
    | _ -> sprintf "%s: C#'s binder" operation

/// For display only: a block nested in another is spliced into it (its variables with it), and
/// the `()` values left mid-block are dropped. The translator nests a block per `unit` statement,
/// builder step and body, each ending in `()`; the renderer would print every one of those as
/// `return null`, though only the outermost is a result. The compiled tree is unchanged.
let private flatten (tree: Expression) =
    let isUnit (e: Expression) = match e with :? ConstantExpression as c -> c.Type = typeof<unit> | _ -> false
    { new ExpressionVisitor() with
        override this.VisitBlock(node) =
            let node = base.VisitBlock node :?> BlockExpression
            let last = node.Expressions.Count - 1
            let variables = ResizeArray node.Variables
            let spliced =
                [ for i, e in Seq.indexed node.Expressions do
                    match e with
                    | :? BlockExpression as inner when i < last || inner.Type = node.Type ->
                        variables.AddRange inner.Variables
                        yield! inner.Expressions
                    | e -> yield e ]
            let kept = spliced |> List.indexed |> List.filter (fun (i, e) -> i = spliced.Length - 1 || not (isUnit e)) |> List.map snd
            Expression.Block(node.Type, variables, kept) :> Expression }
        .Visit tree

/// For display only: the other object constants the tree holds (a block's `SiteCache`, its
/// `NamedOfCache`, a type-argument list) become variables declared at the top, as the sites are,
/// so the renderer shows them as constants with a comment rather than as a type name that reads
/// like a static call. Returns the tree and each variable's comment.
let private hoistConstants (tree: LambdaExpression) =
    let declared = Collections.Generic.Dictionary<obj, ParameterExpression>(HashIdentity.Reference)
    let comments = Collections.Generic.Dictionary<string, string>()
    let describeConstant (value: obj) =
        match value with
        | :? SiteCache<Tuple<string, list<Type>>> | :? SiteCache<string> | :? SiteCache<list<Type>> ->
            Some("siteCache", "This block's call sites per key (member name, type arguments): a new key creates sites, compiling nothing")
        | :? NamedOfCache -> Some("namedOfCache", "This call, compiled once per argument shape (the names, an empty one per positional value): a new shape compiles it")
        | :? list<Type> as ts -> Some("typeArguments", if ts.IsEmpty then "The explicit type arguments: none" else "The explicit type arguments")
        | v when not (isNull v) && v.GetType().Name.StartsWith "SiteCache" -> Some("siteCache", "This block's call sites per key: a new key creates sites, compiling nothing")
        | _ -> None
    let body =
        { new ExpressionVisitor() with
            override _.VisitConstant(node) =
                match node.Value with
                | null -> node :> Expression
                | value ->
                    match declared.TryGetValue value with
                    | true, var -> var :> Expression
                    | _ ->
                        match describeConstant value with
                        | Some(name, comment) ->
                            let var = Expression.Variable(node.Type, name)
                            declared.[value] <- var
                            comments.[name] <- comment
                            var :> Expression
                        | None -> node :> Expression }
            .Visit tree.Body
    let assigns = [ for KeyValue(value, var) in declared -> Expression.Assign(var, Expression.Constant(value, var.Type)) :> Expression ]
    let tree =
        if declared.Count = 0 then tree
        else Expression.Lambda(tree.Type, Expression.Block(body.Type, declared.Values, assigns @ [ body ]), tree.Parameters)
    tree, comments

/// Each hoisted site's local, by name, with what the site does: from the `var = constant` assignments.
let private sitesIn (tree: Expression) =
    let found = Collections.Generic.Dictionary<string, string>()
    { new ExpressionVisitor() with
        override _.VisitBinary(node) =
            match node.Left, node.Right with
            | (:? ParameterExpression as v), (:? ConstantExpression as c) when node.NodeType = ExpressionType.Assign && (c.Value :? CallSite) ->
                found.[v.Name] <- describe (c.Value :?> CallSite).Binder
            | _ -> ()
            base.VisitBinary node }
        .Visit tree |> ignore
    found

/// The renderer prints a constant as its type: say it is one, and what the site does.
let private labelConstants (sites: Collections.Generic.Dictionary<string, string>) (tree: string) =
    Regex.Replace(tree, @"^(\s*)var (\w+) = ((?:CallSite|SiteCache|NamedOfCache|FSharpList)(?:<.*>)?);$", (fun (m: Match) ->
        let indent, name = m.Groups.[1].Value, m.Groups.[2].Value
        let comment = match sites.TryGetValue name with | true, d -> indent + "// " + d + "\n" | _ -> ""
        sprintf "%s%svar %s = <constant %s>;" comment indent name m.Groups.[3].Value), RegexOptions.Multiline)

[<EntryPoint>]
let main args =
#if DEBUG
    eprintfn "Run in Release (-c Release): the docs show the state-machine path, the one users ship."
    exit 2
#endif
    let check = args |> Array.contains "check"
    // The tree of each example, as the library hands it over just before compiling it.
    let trees = Collections.Generic.Dictionary<string, string>()
    let mutable current = ""
    typeof<DlrRun>.Assembly.GetType("FSharp.Interop.Dlr.Translate+TreeHook")
        .GetProperty("Sink", BindingFlags.NonPublic ||| BindingFlags.Public ||| BindingFlags.Static)
        .SetValue(null, Action<Type, LambdaExpression>(fun _ tree ->
            let tree, others = hoistConstants tree
            let comments = sitesIn tree
            for KeyValue(name, comment) in others do comments.[name] <- comment
            trees.[current] <- labelConstants comments ((flatten tree).ToReadableString())))
    for name, run in Examples.all do
        current <- name
        run ()

    let block name =
        match Map.tryFind name sources, trees.TryGetValue name with
        | Some source, (true, tree) -> sprintf "```fsharp\n%s\n```\n\n```csharp\n%s\n```" source (tree.TrimEnd())
        | _ -> failwithf "no example named %s in Examples.fs" name
    let sourceOnly name =
        match Map.tryFind name sources with
        | Some source -> sprintf "```fsharp\n%s\n```" source
        | None -> failwithf "no source named %s in Examples.fs" name
    let marker = Regex(@"(<!-- (tree|source):(\w+) -->).*?(<!-- /\2:\3 -->)", RegexOptions.Singleline)
    // Every marker must be a complete pair naming a real example, and every example must appear
    // somewhere: a deleted region or a mistyped closing marker would otherwise pass unnoticed.
    let opening = Regex(@"<!-- (tree|source):(\w+) -->")
    let closing = Regex(@"<!-- /(tree|source):(\w+) -->")
    let names (r: Regex) (text: string) = [ for m in r.Matches text -> m.Groups.[1].Value + ":" + m.Groups.[2].Value ] |> List.sort
    let problems =
        [ let texts = [ for page in pages -> page, File.ReadAllText(Path.Combine(root, page)) ]
          for page, text in texts do
              if names opening text <> names closing text then
                  yield sprintf "%s: opening markers %A do not match closing markers %A" page (names opening text) (names closing text)
              for m in opening.Matches text do
                  let kind, name = m.Groups.[1].Value, m.Groups.[2].Value
                  if not (sources.ContainsKey name) || (kind = "tree" && not (trees.ContainsKey name)) then
                      yield sprintf "%s: marker %s:%s names no example in Examples.fs" page kind name
          let shown = set [ for _, text in texts do for m in opening.Matches text -> m.Groups.[2].Value ]
          for name in sources.Keys do
              if not (shown.Contains name) then yield sprintf "example %s appears on no page" name ]
    if not problems.IsEmpty then
        for p in problems do eprintfn "trees: %s" p
        exit 1
    let stale =
        [ for page in pages do
            let path = Path.Combine(root, page)
            let text = File.ReadAllText path
            let updated =
                marker.Replace(text, fun m ->
                    let content = if m.Groups.[2].Value = "tree" then block m.Groups.[3].Value else sourceOnly m.Groups.[3].Value
                    m.Groups.[1].Value + "\n" + content + "\n" + m.Groups.[4].Value)
            if updated <> text then
                if not check then File.WriteAllText(path, updated)
                yield page ]
    if check then
        if stale.IsEmpty then printfn "trees: every example in the docs is current"; 0
        else
            for page in stale do eprintfn "trees: %s is stale; run `dotnet run -c Release --project docs/trees`" page
            1
    else
        printfn "trees: %d example(s) rendered; %s" trees.Count (if stale.IsEmpty then "no page changed" else "wrote " + String.Join(", ", stale))
        0
