module FSharp.Interop.Dlr.Analyzers.ReflectedDefinitionAnalyzer

open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols
open FSharp.Compiler.Syntax
open FSharp.Compiler.SyntaxTrivia
open FSharp.Compiler.Text

[<Literal>]
let Code = "DLR001"

/// A `?` operator or `Dlr.*` marker used outside any `dlr { }` block: it is only ever quoted, and
/// executed it throws.
[<Literal>]
let OutsideCode = "DLR002"

/// Two or more `dlr { }` blocks starting on one source line: a block's body is found by the file
/// and line of its `Run` call, so the first call raises DlrTranslationException.
[<Literal>]
let SharedLineCode = "DLR003"

/// A `dlr { }` inside an `inline` function or member: the function is expanded into each caller,
/// so the block's container is built there with the caller's values (a constant has no field at
/// all) and its body is not where the reflected definition says. It cannot work.
[<Literal>]
let InlineCode = "DLR004"

let private isReflectedDefinition (attributes: seq<FSharpAttribute>) =
    attributes
    |> Seq.exists (fun a ->
        try
            let t = a.AttributeType
            t.DisplayName = "ReflectedDefinitionAttribute"
            && (match t.Namespace with Some ns -> ns = "Microsoft.FSharp.Core" | None -> true)
        with _ -> false)

/// Whether the member or any type/module it is declared in carries the attribute. Needed
/// because the typed tree lists a class's members as siblings of the class entity, not under it.
let private memberIsReflected (mfv: FSharpMemberOrFunctionOrValue) =
    let rec entityChain (e: FSharpEntity option) =
        match e with
        | Some e -> isReflectedDefinition e.Attributes || entityChain e.DeclaringEntity
        | None -> false
    isReflectedDefinition mfv.Attributes || entityChain mfv.DeclaringEntity

let private isDlrRun (mfv: FSharpMemberOrFunctionOrValue) =
    mfv.DisplayName = "Run"
    && (match mfv.DeclaringEntity with
        | Some e -> (try e.FullName = "FSharp.Interop.Dlr.DlrBuilder" with _ -> false)
        | None -> false)

/// Ranges of every `dlr.Run(...)` call in an expression.
let rec private runCalls (e: FSharpExpr) : range list =
    let here =
        match e with
        | FSharpExprPatterns.Call(_, mfv, _, _, _) when isDlrRun mfv -> [ e.Range ]
        | _ -> []
    here @ (e.ImmediateSubExpressions |> List.collect runCalls)

let private entityFullName (mfv: FSharpMemberOrFunctionOrValue) =
    match mfv.DeclaringEntity with
    | Some e -> (try e.FullName with _ -> "")
    | None -> ""

/// The operators (`?`, `?<-`, `?+?`, …), the `Dlr.*` markers and `Static<'T>.Overloads`.
let private isMarker (mfv: FSharpMemberOrFunctionOrValue) =
    match entityFullName mfv with
    | "FSharp.Interop.Dlr.Operators" | "FSharp.Interop.Dlr.Dlr" -> true
    | name when name.StartsWith "FSharp.Interop.Dlr.DlrModule." -> true   // the Dlr module's types: Dlr.Static<'T> (not DlrCache, DlrRuntime…)
    | _ -> false

/// Marker uses that are not inside a `dlr.Run(...)` subtree: range and display name. Structural
/// rather than by range, since the synthesized `Run` call's range does not span the block body.
/// The outermost marker of a nested use (`Dlr.item (x |> Dlr.get "A") 0`) is reported once.
let rec private markersOutsideRun (e: FSharpExpr) : (range * string) list =
    match e with
    | FSharpExprPatterns.Call(_, mfv, _, _, _) when isDlrRun mfv -> []
    | FSharpExprPatterns.Call(_, mfv, _, _, _) when isMarker mfv -> [ e.Range, mfv.DisplayName ]
    | _ -> e.ImmediateSubExpressions |> List.collect markersOutsideRun

/// A block with no reflected definition around it, and the declaration-level binding it sits in.
type private Finding =
    { Block: range
      /// The function or member the compiler stores a definition for, for the message; None for module-level code.
      Binding: FSharpMemberOrFunctionOrValue option }

/// Every `dlr.Run(...)` in an `inline` function or member, with the binding.
let rec private inInline (decls: FSharpImplementationFileDeclaration list) : (range * FSharpMemberOrFunctionOrValue) list =
    decls
    |> List.collect (fun decl ->
        match decl with
        | FSharpImplementationFileDeclaration.Entity(_, subDecls) -> inInline subDecls
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(mfv, _, body) ->
            let isInline =
                try mfv.InlineAnnotation = FSharpInlineAnnotation.AlwaysInline || mfv.InlineAnnotation = FSharpInlineAnnotation.AggressiveInline
                with _ -> false
            if isInline then runCalls body |> List.map (fun r -> r, mfv) else []
        | FSharpImplementationFileDeclaration.InitAction _ -> [])

let private analyzeInline (typedTree: FSharpImplementationFileContents option) : Message list =
    match typedTree with
    | None -> []
    | Some contents ->
        inInline contents.Declarations
        |> List.map (fun (m, mfv) ->
            { Type = "dlr { } in an inline function"
              Message = sprintf "dlr { } inside the inline function or member '%s' cannot work: the function is expanded into every caller, where the block's captured values are inlined away and its body is not where the reflected definition says. Remove 'inline', or move the block into a function that is not inline." mfv.DisplayName
              Code = InlineCode
              Severity = Severity.Error
              Range = m
              Fixes = [] })

let rec private findInDeclarations (reflected: bool) (decls: FSharpImplementationFileDeclaration list) : Finding list =
    decls
    |> List.collect (fun decl ->
        match decl with
        | FSharpImplementationFileDeclaration.Entity(entity, subDecls) ->
            findInDeclarations (reflected || isReflectedDefinition entity.Attributes) subDecls
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(mfv, _, body) ->
            if reflected || memberIsReflected mfv then []
            else runCalls body |> List.map (fun r -> { Block = r; Binding = Some mfv })
        | FSharpImplementationFileDeclaration.InitAction expr ->
            // Module-level `do` compiles into the static initializer, which has no reflected
            // definition even under a module attribute: always a finding, never fixable in place.
            runCalls expr |> List.map (fun r -> { Block = r; Binding = None }))

/// The `let` / `member` keyword position of the outermost syntax binding containing `m`, for
/// the fix: the attribute goes on its own line before the keyword, at the keyword's indentation.
/// Outermost because a local function inside a member is a closure and cannot carry the
/// attribute; only the declaration-level binding the compiler stores can.
let private bindingKeyword (tree: ParsedInput) (m: range) : range option =
    let mutable best: (range * range) option = None   // (binding range, keyword range)
    let consider (binding: SynBinding) =
        let full = binding.RangeOfBindingWithRhs
        if Range.rangeContainsRange full m then
            match best with
            | Some(current, _) when Range.rangeContainsRange current full -> ()
            | _ -> best <- Some(full, binding.Trivia.LeadingKeyword.Range)
    let walker =
        { new ASTCollecting.SyntaxCollectorBase() with
            override _.WalkBinding(_, binding) = consider binding }
    ASTCollecting.walkAst walker tree
    best |> Option.map snd

/// Marker uses whose range is not inside any `dlr.Run(...)` call in the same declaration.
let rec private outsideBlocks (decls: FSharpImplementationFileDeclaration list) : (range * string) list =
    let inBody (body: FSharpExpr) =
        markersOutsideRun body
    decls
    |> List.collect (fun decl ->
        match decl with
        | FSharpImplementationFileDeclaration.Entity(_, subDecls) -> outsideBlocks subDecls
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(_, _, body) -> inBody body
        | FSharpImplementationFileDeclaration.InitAction expr -> inBody expr)

let private analyzeOutside (typedTree: FSharpImplementationFileContents option) : Message list =
    match typedTree with
    | None -> []
    | Some contents ->
        outsideBlocks contents.Declarations
        |> List.map (fun (m, name) ->
            { Type = "dlr marker outside dlr { }"
              Message = sprintf "'%s' is only meaningful inside dlr { }: it is inspected as a quotation, never executed, and calling it throws InvalidOperationException." name
              Code = OutsideCode
              Severity = Severity.Error
              Range = m
              Fixes = [] })

/// The outermost `dlr.Run(...)` calls in an expression: a block nested in another is compiled as
/// part of it and has no site of its own, so it does not count.
let rec private outermostRuns (e: FSharpExpr) : range list =
    match e with
    | FSharpExprPatterns.Call(_, mfv, _, _, _) when isDlrRun mfv -> [ e.Range ]
    | _ -> e.ImmediateSubExpressions |> List.collect outermostRuns

/// Every outermost `dlr.Run(...)` call in the file, from all declarations.
let rec private allRuns (decls: FSharpImplementationFileDeclaration list) : range list =
    decls
    |> List.collect (fun decl ->
        match decl with
        | FSharpImplementationFileDeclaration.Entity(_, subDecls) -> allRuns subDecls
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(_, _, body) -> outermostRuns body
        | FSharpImplementationFileDeclaration.InitAction expr -> outermostRuns expr)

let private analyzeSharedLines (typedTree: FSharpImplementationFileContents option) : Message list =
    match typedTree with
    | None -> []
    | Some contents ->
        allRuns contents.Declarations
        |> List.distinct
        |> List.groupBy (fun r -> r.FileName, r.StartLine)
        |> List.collect (fun ((_, line), ranges) ->
            if ranges.Length < 2 then []
            else
                ranges
                |> List.map (fun r ->
                    { Type = "dlr { } blocks on one line"
                      Message = sprintf "%d dlr { } blocks start on line %d. A block is found by the line of its Run call, so the first call raises DlrTranslationException; put each dlr { } on its own line." ranges.Length line
                      Code = SharedLineCode
                      Severity = Severity.Error
                      Range = r
                      Fixes = [] }))

let private analyze (tree: ParsedInput) (typedTree: FSharpImplementationFileContents option) : Message list =
    analyzeOutside typedTree
    @ analyzeSharedLines typedTree
    @ analyzeInline typedTree
    @ match typedTree with
      | None -> []
      | Some contents ->
        findInDeclarations false contents.Declarations
        |> List.map (fun finding ->
            let fixes =
                match finding.Binding, bindingKeyword tree finding.Block with
                | None, _ -> []
                | Some _, Some keyword ->
                    let insertAt = Range.mkRange keyword.FileName keyword.Start keyword.Start
                    let indent = String.replicate keyword.StartColumn " "
                    [ { FromRange = insertAt; FromText = ""; ToText = "[<ReflectedDefinition>]\n" + indent } ]
                | Some _, None -> []
            let where =
                match finding.Binding with
                | Some mfv -> sprintf "'%s'" mfv.DisplayName
                | None -> "this module-level code; move the block into a function"
            { Type = "dlr { } without ReflectedDefinition"
              Message =
                sprintf "dlr { } needs [<ReflectedDefinition>] on the function or member that contains it, here %s (the attribute on a whole module also works, but only when everything in it can be quoted). Without it the first call raises DlrTranslationException." where
              Code = Code
              Severity = Severity.Error
              Range = finding.Block
              Fixes = fixes })

[<CliAnalyzer "FSharp.Interop.Dlr ReflectedDefinition">]
let cliAnalyzer (ctx: CliContext) : Async<Message list> =
    async { return analyze ctx.ParseFileResults.ParseTree ctx.TypedTree }

[<EditorAnalyzer "FSharp.Interop.Dlr ReflectedDefinition">]
let editorAnalyzer (ctx: EditorContext) : Async<Message list> =
    async { return analyze ctx.ParseFileResults.ParseTree ctx.TypedTree }
