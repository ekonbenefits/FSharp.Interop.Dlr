module FSharp.Interop.Dlr.Analyzers.ReflectedDefinitionAnalyzer

open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols
open FSharp.Compiler.Syntax
open FSharp.Compiler.SyntaxTrivia
open FSharp.Compiler.Text

[<Literal>]
let Code = "DLR001"

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

/// A block with no reflected definition around it, and the declaration-level binding it sits in.
type private Finding =
    { Block: range
      /// The function or member the compiler stores a definition for, for the message; None for module-level code.
      Binding: FSharpMemberOrFunctionOrValue option }

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

let private analyze (tree: ParsedInput) (typedTree: FSharpImplementationFileContents option) : Message list =
    match typedTree with
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
