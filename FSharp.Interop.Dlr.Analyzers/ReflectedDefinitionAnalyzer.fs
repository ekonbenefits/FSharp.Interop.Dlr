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

/// A `dlr { }` inside an `inline` function or member: in Release the function is expanded into
/// each caller, so the block's container is built there with the caller's values (a constant has
/// no field at all) and its body is not where the reflected definition says. A Debug build does
/// not expand inline functions and calls it as a method, so it only appears to work there.
[<Literal>]
let InlineCode = "DLR004"

/// An argument marker (`Dlr.named`, `Dlr.namedOf`, `Dlr.argsOf`, `Dlr.typeArgs`, `Dlr.typeArgsOf`) anywhere
/// but as an argument of a call — a member call, `Dlr.invoke`, `Dlr.call` / `Dlr.apply`,
/// `Dlr.new'` — or misplaced there: type arguments not first, `namedOf` twice in one call;
/// `Dlr.named` on a record in a variable; `Dlr.Static<T>.Overloads` anywhere but as a call's
/// target; `Dlr.call x` read at a non-function type. Each is a DlrTranslationException at the
/// block's first call.
[<Literal>]
let ArgumentMarkerCode = "DLR005"

/// A function or member holding a `dlr { }` whose reflected definition FSharp.Core will not
/// decode: it compiles and stores the quotation, but reading it back throws, and every block in
/// the member then fails at run time (named as such by the not-found error). Known cause:
/// `typeof<System.Void>` (the only spelling of `System.Void` F# allows).
[<Literal>]
let UndecodableCode = "DLR006"

/// A `dlr { }` that reaches two values of one name, one through a local function or alias:
/// `let x = …; let f () = x; let x = …; dlr { … f () … x … }`. Where the Release optimizer
/// inlines `f`, the
/// block's container holds both as `x` and `x0`, which nothing tells apart, and the first call
/// raises DlrTranslationException (#196). A warning: whether `f` is inlined is the optimizer's
/// call, and a Debug build never inlines it.
[<Literal>]
let ShadowedCode = "DLR007"

/// A reflected definition (any, with a block or not) that calls or reads a module value its
/// signature file leaves out. The Release optimizer may inline such a value at every call and
/// erase it; the stored quotation still names it, so FSharp.Core cannot decode the assembly's
/// reflected definitions at all, and every `dlr { }` in the assembly fails (#213). A warning:
/// whether it is inlined is the optimizer's call (small functions are), and a Debug build does not.
[<Literal>]
let HiddenBySignatureCode = "DLR008"

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

/// A marker's name as the run-time outside-a-block message prints it: `?`, `Dlr.get`,
/// `Dlr.Static<T>.Overloads`.
let private markerName (mfv: FSharpMemberOrFunctionOrValue) =
    if mfv.CompiledName.StartsWith "op_" then mfv.DisplayName.Trim([| '('; ')'; ' ' |])
    elif mfv.DisplayName = "Overloads" then "Dlr.Static<T>.Overloads"
    else "Dlr." + mfv.DisplayName

/// Marker uses that are not inside a `dlr.Run(...)` subtree: range and name. Structural
/// rather than by range, since the synthesized `Run` call's range does not span the block body.
/// The outermost marker of a nested use (`Dlr.item (x |> Dlr.get "A") 0`) is reported once.
let rec private markersOutsideRun (e: FSharpExpr) : (range * string) list =
    match e with
    | FSharpExprPatterns.Call(_, mfv, _, _, _) when isDlrRun mfv -> []
    | FSharpExprPatterns.Call(_, mfv, _, _, _) when isMarker mfv -> [ e.Range, markerName mfv ]
    | _ -> e.ImmediateSubExpressions |> List.collect markersOutsideRun

/// A block with no reflected definition around it, and the declaration-level binding it sits in.
type private Finding =
    { Block: range
      /// The function or member the compiler stores a definition for, for the message; None for module-level code.
      Binding: FSharpMemberOrFunctionOrValue option }

let private isInline (mfv: FSharpMemberOrFunctionOrValue) =
    try mfv.InlineAnnotation = FSharpInlineAnnotation.AlwaysInline || mfv.InlineAnnotation = FSharpInlineAnnotation.AggressiveInline
    with _ -> false

/// Every `dlr.Run(...)` in a local `let inline` function inside `e`, with the binding. (A local
/// applied exactly once happens to resolve at run time through the once-called-function path;
/// applied twice it fails in Release. `inline` on it buys nothing either way.)
let rec private inLocalInline (e: FSharpExpr) : (range * FSharpMemberOrFunctionOrValue) list =
    let here =
        match e with
        | FSharpExprPatterns.Let((mfv, def, _), _) when isInline mfv -> runCalls def |> List.map (fun r -> r, mfv)
        | _ -> []
    here @ (e.ImmediateSubExpressions |> List.collect inLocalInline)

/// Every `dlr.Run(...)` in an `inline` function or member, declaration-level or local, with the binding.
let rec private inInline (decls: FSharpImplementationFileDeclaration list) : (range * FSharpMemberOrFunctionOrValue) list =
    decls
    |> List.collect (fun decl ->
        match decl with
        | FSharpImplementationFileDeclaration.Entity(_, subDecls) -> inInline subDecls
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(mfv, _, body) ->
            if isInline mfv then runCalls body |> List.map (fun r -> r, mfv) else inLocalInline body
        | FSharpImplementationFileDeclaration.InitAction expr -> inLocalInline expr)

let private analyzeInline (typedTree: FSharpImplementationFileContents option) : Message list =
    match typedTree with
    | None -> []
    | Some contents ->
        inInline contents.Declarations
        |> List.map (fun (m, mfv) ->
            { Type = "dlr { } in an inline function"
              Message = sprintf "dlr { } inside the inline function or member '%s' fails in Release: the function is expanded into every caller, where the block's captured values are inlined away and its body is not where the reflected definition says (a Debug build calls it as a method, so it only appears to work). Remove 'inline', or move the block into a function that is not inline." mfv.DisplayName
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

/// `Dlr.out` (a property: its getter) / `Dlr.ref`: the byref argument markers (#131).
let private isByRefMarker (mfv: FSharpMemberOrFunctionOrValue) =
    isMarker mfv && (match mfv.DisplayName, mfv.CompiledName with ("out" | "outAs" | "ref"), _ | _, "get_out" -> true | _ -> false)

/// `Dlr.named` / `namedOf` / `typeArgs` / `typeArgsOf` / `out` / `ref`: meaningful only as an argument.
let private isArgumentMarker (mfv: FSharpMemberOrFunctionOrValue) =
    isByRefMarker mfv || (isMarker mfv && (match mfv.DisplayName with "named" | "namedOf" | "argsOf" | "typeArgs" | "typeArgsOf" -> true | _ -> false))

/// Whether `body` ends in an application of the let-bound `v` (through the `let ai = tupledArg.i`
/// re-bindings of the tupled eta-expansion).
let rec private appliesLet (v: FSharpMemberOrFunctionOrValue) (body: FSharpExpr) =
    match body with
    | FSharpExprPatterns.Let(_, rest) -> appliesLet v rest
    | FSharpExprPatterns.Application(FSharpExprPatterns.Value v', _, _) -> v'.IsEffectivelySameAs v
    | _ -> false

/// The marker a function expression is headed by, descending through pipes, lambdas (a partial
/// application of `Dlr.invoke` is an eta-expanded lambda chain), coercions and lets — with the
/// number of lambdas passed, since an applied argument maps to a lambda parameter.
let rec private head (f: FSharpExpr) (lambdas: int) : (FSharpMemberOrFunctionOrValue * int) option =
    match f with
    | FSharpExprPatterns.Call(_, mfv, _, _, _) when isMarker mfv -> Some(mfv, lambdas)
    // The tupled eta-expansion of a call with a tuple of arguments on a target whose static type
    // is not `obj`: `let clo = (f ())?M in fun tupledArg -> let a0 = tupledArg.0 in … clo (a0, …)`;
    // the head is the let's value.
    | FSharpExprPatterns.Let((v, value, _), FSharpExprPatterns.Lambda(_, body)) when appliesLet v body -> head value lambdas
    | FSharpExprPatterns.Call(_, mfv, _, _, [ _; g ]) when mfv.CompiledName = "op_PipeRight" -> head g lambdas
    | FSharpExprPatterns.Call(_, mfv, _, _, [ g; _ ]) when mfv.CompiledName = "op_PipeLeft" -> head g lambdas
    | FSharpExprPatterns.Application(g, _, _) -> head g lambdas
    | FSharpExprPatterns.Lambda(_, body) -> head body (lambdas + 1)
    | FSharpExprPatterns.Coerce(_, inner) -> head inner lambdas
    | FSharpExprPatterns.Let(_, body) -> head body lambdas
    | _ -> None

/// The call forms whose arguments may hold argument markers: `?` / `Dlr.get` / `Dlr.call`
/// applied to arguments, and `Dlr.invoke name args target`, `Dlr.apply args target`,
/// `Dlr.new'(args…)`, written directly or partially applied through a pipe. The argument
/// expressions of such a call, if `e` is one.
/// The argument expressions of a call form, if `e` is one, and whether it is a *member* call
/// (`?` / `Dlr.get` / `Dlr.invoke`): only those take type arguments; `Dlr.call` / `apply` /
/// `new'` invoke a value or a constructor and reject them.
let private callArgumentExprs (e: FSharpExpr) : (FSharpExpr list * bool) option =
    match e with
    | FSharpExprPatterns.Application(f, _, args) ->
        // A direct `Dlr.get "M" w (a, b)` comes as one flattened list, `[name; target; (a, b)]`:
        // the call's arguments are the last applied expression.
        match head f 0 with
        | Some(mfv, _) when (match mfv.CompiledName with "op_Dynamic" | "get" -> true | _ -> false) -> Some([ List.last args ], true)
        | Some(mfv, _) when mfv.CompiledName = "call" -> Some([ List.last args ], false)
        | Some(mfv, lambdas) when lambdas > 0 && (mfv.DisplayName = "invoke" || mfv.DisplayName = "apply") ->
            // The eta-expanded partial application: the applied arguments fill the lambda
            // parameters in order; the `args` parameter is the second of invoke, the first of apply.
            let index = if mfv.DisplayName = "invoke" then 1 else 0
            if args.Length > index then Some([ args.[index] ], mfv.DisplayName = "invoke") else None
        | _ -> None
    | FSharpExprPatterns.Call(_, mfv, _, _, args) when isMarker mfv ->
        match mfv.DisplayName, args with
        | "invoke", [ _; arg; _ ] -> Some([ arg ], true)
        | "apply", [ arg; _ ] -> Some([ arg ], false)
        | "new'", args -> Some(args, false)
        | _ -> None
    | _ -> None

/// Every expression in `e`, with the `let`-bound definitions in scope (F# lowers a call's tuple
/// of arguments to a `let` when the function is a value, e.g. through a pipe).
let rec private allExprs (e: FSharpExpr) : FSharpExpr list = e :: (e.ImmediateSubExpressions |> List.collect allExprs)

let private letDefinitions (e: FSharpExpr) =
    allExprs e
    |> List.choose (function FSharpExprPatterns.Let((v, def, _), _) -> Some(v, def) | _ -> None)

/// Misplaced argument markers inside one block: range and message.
let private misplacedMarkers (block: FSharpExpr) : (range * string) list =
    let exprs = allExprs block
    let lets = letDefinitions block
    /// An argument expression with a compiler-generated `let`-bound variable followed to its
    /// definition (F# lowers a call's tuple that way), and the coercion to `obj` of a `Dlr.new'`
    /// argument stripped. A `let` the user wrote is not followed: the translator does not
    /// either, and a marker in it executes.
    let rec resolve (a: FSharpExpr) =
        match a with
        | FSharpExprPatterns.Value v when v.IsCompilerGenerated ->
            match lets |> List.tryFind (fun (lv, _) -> lv.IsEffectivelySameAs v) with
            | Some(_, def) -> resolve def
            | None -> a
        | FSharpExprPatterns.Coerce(_, inner) -> resolve inner
        | _ -> a
    /// The items of one call's argument list: a tuple's elements, else the one expression.
    let items (a: FSharpExpr) =
        match resolve a with
        | FSharpExprPatterns.NewTuple(_, items) -> items |> List.map resolve
        | single -> [ single ]
    let argumentLists = exprs |> List.choose callArgumentExprs |> List.map (fun (args, isMember) -> List.collect items args, isMember)
    let argumentRoots = argumentLists |> List.collect fst
    let markers = exprs |> List.choose (function FSharpExprPatterns.Call(_, mfv, _, _, _) as m when isArgumentMarker mfv -> Some(m, mfv) | _ -> None)
    /// A marker is placed if it *is* an argument root (its range equal), not merely inside one
    /// (a marker inside another marker's list is not an argument).
    let placed (m: FSharpExpr) = argumentRoots |> List.exists (fun r -> Range.equals r.Range m.Range)
    let outOfPlace =
        markers
        |> List.filter (fun (m, _) -> not (placed m))
        |> List.map (fun (m, mfv) -> m.Range, sprintf "Dlr.%s is only meaningful as an argument of a call (a member call, Dlr.invoke, Dlr.call / Dlr.apply, Dlr.new'); here it would raise DlrTranslationException at the block's first call." mfv.DisplayName)
    // `Dlr.named` takes the record literal itself: the names are read from the quotation, so a
    // record held in a variable is a translation error.
    let namedNotLiteral =
        markers
        |> List.choose (fun (m, mfv) ->
            match m with
            | FSharpExprPatterns.Call(_, _, _, _, [ arg ]) when mfv.DisplayName = "named" ->
                // A literal of several fields is let-bound field by field (evaluation order),
                // as the translator also peels.
                let rec peel (a: FSharpExpr) = match a with FSharpExprPatterns.Let(_, body) -> peel body | a -> a
                match peel (resolve arg) with
                | FSharpExprPatterns.NewAnonRecord _ | FSharpExprPatterns.NewRecord _ -> None
                | _ -> Some(m.Range, "Dlr.named takes a record literal written in the call, Dlr.named {| p = 2 |}: the argument names are read from the block's quotation, so a record held in a variable would raise DlrTranslationException at the block's first call. For names from data use Dlr.namedOf.")
            | _ -> None)
    // `Dlr.Static<T>.Overloads` is a call target only: C#'s binder has no static member get, set
    // or index, and plain F# already has those (`T.P`).
    let isOverloads (e: FSharpExpr) = match e with FSharpExprPatterns.Call(_, mfv, _, _, _) when isMarker mfv && mfv.DisplayName = "Overloads" -> true | _ -> false
    /// A call form seen from its outermost expression: the marker at its head, its own
    /// arguments, the lambdas passed (an eta-expanded partial application) and the arguments
    /// applied along the way, innermost first (applications and pipes), which fill those lambdas.
    let rec describe (f: FSharpExpr) : (FSharpMemberOrFunctionOrValue * FSharpExpr list * int * FSharpExpr list) option =
        match f with
        | FSharpExprPatterns.Call(_, mfv, _, _, args) when isMarker mfv -> Some(mfv, args, 0, [])
        | FSharpExprPatterns.Call(_, mfv, _, _, [ x; g ]) when mfv.CompiledName = "op_PipeRight" -> describe g |> Option.map (fun (m, a, l, applied) -> m, a, l, applied @ [ x ])
        | FSharpExprPatterns.Call(_, mfv, _, _, [ g; x ]) when mfv.CompiledName = "op_PipeLeft" -> describe g |> Option.map (fun (m, a, l, applied) -> m, a, l, applied @ [ x ])
        | FSharpExprPatterns.Application(g, _, args) -> describe g |> Option.map (fun (m, a, l, applied) -> m, a, l, applied @ args)
        | FSharpExprPatterns.Lambda(_, body) -> describe body |> Option.map (fun (m, a, l, applied) -> m, a, l + 1, applied)
        | FSharpExprPatterns.Coerce(_, inner) -> describe inner
        | FSharpExprPatterns.Let(_, body) -> describe body
        | _ -> None
    let placedStatics =
        exprs
        |> List.choose (fun e ->
            match describe e with
            | Some(mfv, args, lambdas, applied) ->
                // The target parameter: first of `?`, last of `Dlr.get` / `Dlr.invoke`. Written
                // direct it is in the marker's own arguments; eta-expanded, the applied ones.
                let index = if mfv.CompiledName = "op_Dynamic" then 0 elif mfv.CompiledName = "get" then 1 elif mfv.CompiledName = "invoke" then 2 else -1
                let target = if index < 0 then None elif lambdas > 0 then List.tryItem index applied else List.tryItem index args
                // `Dlr.invoke` is a call as it is; `?` / `Dlr.get` only once applied to arguments
                // beyond the eta-expansion's own parameters, else they read a member.
                let isCall = mfv.CompiledName = "invoke" || applied.Length > lambdas
                match target with
                | Some t when isCall && isOverloads t -> Some t
                | _ -> None
            | None -> None)
    let staticsOutOfPlace =
        exprs
        |> List.filter isOverloads
        |> List.filter (fun o -> not (placedStatics |> List.exists (fun p -> Range.equals p.Range o.Range)))
        |> List.map (fun o -> o.Range, "Dlr.Static<T>.Overloads is only meaningful as the target of a call, Dlr.Static<T>.Overloads?M(…) or |> Dlr.invoke \"M\" args: C#'s binder has no static member get, set or index, and a static property is T.P in plain F#. Here it would raise DlrTranslationException at the block's first call.")
    // `Dlr.call x` read at a non-function type: the translator has no meaning for it. Applied,
    // it is a call (the typed tree's type of an over-applied call is not its function type, so
    // the application is what tells).
    let appliedCalls =
        exprs
        |> List.collect (fun e ->
            match e with
            | FSharpExprPatterns.Application(f, _, _) ->
                let rec calls (f: FSharpExpr) =
                    match f with
                    | FSharpExprPatterns.Call(_, mfv, _, _, _) when isMarker mfv && mfv.CompiledName = "call" -> [ f ]
                    | FSharpExprPatterns.Call(_, mfv, _, _, [ _; g ]) when mfv.CompiledName = "op_PipeRight" -> calls g
                    | FSharpExprPatterns.Call(_, mfv, _, _, [ g; _ ]) when mfv.CompiledName = "op_PipeLeft" -> calls g
                    | FSharpExprPatterns.Application(g, _, _) -> calls g
                    | FSharpExprPatterns.Lambda(_, body) -> calls body
                    | FSharpExprPatterns.Coerce(_, inner) -> calls inner
                    | FSharpExprPatterns.Let(_, body) -> calls body
                    | _ -> []
                calls f
            | _ -> [])
    let callNotFunction =
        exprs
        |> List.choose (fun e ->
            match e with
            | FSharpExprPatterns.Call(_, mfv, _, _, [ _ ]) when isMarker mfv && mfv.CompiledName = "call" && not e.Type.IsFunctionType && not e.Type.IsGenericParameter
                                                                  && not (appliedCalls |> List.exists (fun a -> Range.equals a.Range e.Range)) ->
                Some(e.Range, "Dlr.call read at a non-function type would raise DlrTranslationException at the block's first call: a value read as a type is Dlr.implicit; to invoke, apply it, Dlr.call x (a, b), or read it at a function type.")
            | _ -> None)
    let isByRefArg (a: FSharpExpr) = match a with FSharpExprPatterns.Call(_, mfv, _, _, _) -> isByRefMarker mfv | _ -> false
    let outCount (args: FSharpExpr list) =
        args |> List.filter (function FSharpExprPatterns.Call(_, mfv, _, _, _) -> isByRefMarker mfv && mfv.DisplayName <> "ref" | _ -> false) |> List.length
    // `Dlr.ref` takes a `let mutable`: its value goes in and the method's write is assigned back.
    let refNotMutable =
        markers
        |> List.choose (fun (m, mfv) ->
            match m with
            | FSharpExprPatterns.Call(_, _, _, _, [ arg ]) when isByRefMarker mfv && mfv.DisplayName = "ref" ->
                match resolve arg with
                | FSharpExprPatterns.Value v when v.IsMutable -> None
                | _ -> Some(m.Range, "Dlr.ref takes a let mutable (its value goes in, and the method's write is assigned back to it); here it would raise DlrTranslationException at the block's first call.")
            | _ -> None)
    // A call with Dlr.out / Dlr.ref: its result shaped as F# returns out parameters. (`Dlr.new'`
    // returns its T: an out there is reported below instead.)
    let isNew (e: FSharpExpr) = match e with FSharpExprPatterns.Call(_, mfv, _, _, _) -> isMarker mfv && mfv.DisplayName = "new'" | _ -> false
    let byRefCalls =
        exprs
        |> List.choose (fun e ->
            match callArgumentExprs e with
            | Some(args, _) when not (isNew e) ->
                let args = List.collect items args
                if args |> List.exists isByRefArg then Some(e, args) else None
            | _ -> None)
        |> List.distinctBy (fun (e, _) -> e.Range)
    let byRefShape =
        byRefCalls
        |> List.choose (fun (e, args) ->
            // Through abbreviations: `unit` is one (of Microsoft.FSharp.Core.Unit), and a tuple may be too.
            let rec unabbreviated (t: FSharpType) = if t.IsAbbreviation then unabbreviated t.AbbreviatedType else t
            // Each out's stated type (`Dlr.outAs<'T> ()`), or None to take it from the result's shape.
            let stated =
                args |> List.choose (fun a ->
                    match a with
                    | FSharpExprPatterns.Call(_, mfv, _, methodTypeArgs, _) when isByRefMarker mfv && mfv.DisplayName <> "ref" ->
                        Some(if mfv.DisplayName = "outAs" then List.tryHead methodTypeArgs |> Option.map unabbreviated else None)
                    | _ -> None)
            let n = stated.Length
            // An eta-expanded partial application (`w |> Dlr.invoke "M" (1, Dlr.out)`, `|> Dlr.apply`)
            // is typed as the function still awaiting its target: the result is past those arrows.
            let remaining =
                match e with
                | FSharpExprPatterns.Application(f, _, applied) ->
                    (match head f 0 with Some(_, lambdas) when lambdas > applied.Length -> lambdas - applied.Length | _ -> 0)
                | _ -> 0
            let rec resultPast (k: int) (t: FSharpType) = if k > 0 && t.IsFunctionType then resultPast (k - 1) t.GenericArguments.[1] else t
            let t = unabbreviated (resultPast remaining e.Type)
            let isUnit = t.HasTypeDefinition && (try t.TypeDefinition.TryFullName = Some "Microsoft.FSharp.Core.Unit" with _ -> false)
            // As the translator: the shapes in order, the first that agrees with every stated type (a
            // type still generic agrees, as it cannot be told yet).
            let agrees (outs: FSharpType list) =
                List.forall2 (fun (s: FSharpType option) (o: FSharpType) ->
                    match s with
                    | Some s -> let o = unabbreviated o in s.IsGenericParameter || o.IsGenericParameter || s.Equals o
                    | None -> true) stated outs
            let fits =
                if n = 0 || t.IsGenericParameter then true
                elif isUnit then false
                else
                    // A reference or struct tuple; the typed tree does not call a one-element
                    // ValueTuple / Tuple written out a tuple, but the translator (reflection) does.
                    let oneTuple = t.HasTypeDefinition && (try (match t.TypeDefinition.TryFullName with Some ("System.ValueTuple`1" | "System.Tuple`1") -> true | _ -> false) with _ -> false)
                    let elements = if t.IsTupleType || oneTuple then List.ofSeq t.GenericArguments else []
                    (elements.Length = n + 1 && agrees elements.Tail)
                    || (n >= 2 && elements.Length = n && agrees elements)
                    // The bare value of a tuple type only when Dlr.outAs states it (as the translator).
                    || (n = 1 && (elements.IsEmpty || (match stated with [ Some s ] -> s.Equals t | _ -> false)) && agrees [ t ])
            if fits then None
            else Some(e.Range, sprintf "a call with %d Dlr.out argument(s) whose result type does not fit: the result is the return value then each out as a tuple, the outs alone as a tuple of two or more, or the one out's value, the first agreeing with every type Dlr.outAs states; here it would raise DlrTranslationException at the block's first call." n))
        |> List.distinctBy fst
    // `Dlr.new'<T>` returns T: no room for an out value (a ref writes back to its variable).
    let outInNew =
        exprs
        |> List.choose (fun e ->
            match e with
            | FSharpExprPatterns.Call(_, _, _, _, args) when isNew e ->
                args |> List.map resolve |> List.tryFind (fun a -> isByRefArg a && outCount [ a ] = 1)
                |> Option.map (fun a -> a.Range, "Dlr.out / Dlr.outAs in Dlr.new': its result is the constructed T, with no room for an out value (Dlr.ref writes back to a variable); here it would raise DlrTranslationException at the block's first call.")
            | _ -> None)
    let perList =
        argumentLists
        |> List.collect (fun (args, isMember) ->
            let markerNamed (name: string) (a: FSharpExpr) = match a with FSharpExprPatterns.Call(_, mfv, _, _, _) when isArgumentMarker mfv && mfv.DisplayName = name -> true | _ -> false
            let twice =
                [ for name in [ "namedOf"; "argsOf" ] do
                    match args |> List.filter (markerNamed name) with
                    | _ :: second :: _ -> yield second.Range, sprintf "Dlr.%s twice in one call: concatenate the lists into one Dlr.%s." name name
                    | _ -> () ]
            // Named arguments are the call's trailing ones: nothing positional after Dlr.namedOf.
            let afterNamed =
                match args |> List.tryFindIndex (markerNamed "namedOf") with
                | Some i ->
                    args |> List.skip (i + 1)
                    |> List.filter (fun a -> not (markerNamed "named" a) && not (markerNamed "namedOf" a))
                    |> List.map (fun a -> a.Range, "a positional argument after Dlr.namedOf: named arguments come last.")
                | None -> []
            let typeArgs =
                args
                |> List.mapi (fun i a -> i, a)
                |> List.choose (fun (i, a) ->
                    match a with
                    | FSharpExprPatterns.Call(_, mfv, _, _, _) when isArgumentMarker mfv && mfv.DisplayName.StartsWith "typeArgs" ->
                        if not isMember then Some(a.Range, sprintf "Dlr.%s only applies to a member call (x?Name(…), Dlr.get, Dlr.invoke); a value invoked with Dlr.call / Dlr.apply or a constructor takes no type arguments." mfv.DisplayName)
                        elif i > 0 then Some(a.Range, sprintf "Dlr.%s must be the first argument." mfv.DisplayName)
                        else None
                    | _ -> None)
            let byRefs =
                match args |> List.filter isByRefArg with
                | [] -> []
                | byRefArgs ->
                    let splat = args |> List.exists (fun a -> markerNamed "namedOf" a || markerNamed "argsOf" a)
                    if splat then [ (List.head byRefArgs).Range, "Dlr.out / Dlr.outAs / Dlr.ref with Dlr.namedOf / Dlr.argsOf in one call: not supported (the outs' types are fixed by the result, the splat's arity is not); here it would raise DlrTranslationException at the block's first call." ]
                    else []
            twice @ afterNamed @ typeArgs @ byRefs)
    outOfPlace @ namedNotLiteral @ staticsOutOfPlace @ callNotFunction @ refNotMutable @ byRefShape @ outInNew @ perList

/// The outermost `dlr.Run(...)` subtrees of `e`.
let rec private blocksIn (e: FSharpExpr) : FSharpExpr list =
    match e with
    | FSharpExprPatterns.Call(_, mfv, _, _, _) when isDlrRun mfv -> [ e ]
    | _ -> e.ImmediateSubExpressions |> List.collect blocksIn

/// Misplaced argument markers in every block of the file (outside a block any marker is DLR002
/// already).
let private analyzeArgumentMarkers (typedTree: FSharpImplementationFileContents option) : Message list =
    match typedTree with
    | None -> []
    | Some contents ->
        let rec decls (ds: FSharpImplementationFileDeclaration list) : FSharpExpr list =
            ds |> List.collect (function
                | FSharpImplementationFileDeclaration.Entity(_, sub) -> decls sub
                | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(_, _, body) -> blocksIn body
                | FSharpImplementationFileDeclaration.InitAction expr -> blocksIn expr)
        decls contents.Declarations
        |> List.collect misplacedMarkers
        |> List.distinctBy (fun (r, _) -> r.StartLine, r.StartColumn, r.EndLine, r.EndColumn)
        |> List.map (fun (r, message) ->
            { Type = "dlr argument marker out of place"
              Message = message
              Code = ArgumentMarkerCode
              Severity = Severity.Error
              Range = r
              Fixes = [] })

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

/// Whether `t` is `System.Void` or has it among its type arguments, at any depth.
let rec private mentionsVoid (t: FSharpType) =
    try
        (t.HasTypeDefinition && t.TypeDefinition.TryFullName = Some "System.Void")
        || (t.HasTypeDefinition && t.GenericArguments |> Seq.exists mentionsVoid)
        || (t.IsFunctionType && t.GenericArguments |> Seq.exists mentionsVoid)
        || (t.IsTupleType && t.GenericArguments |> Seq.exists mentionsVoid)
    with _ -> false

/// The expressions in `e` that put `System.Void` in a type argument: `typeof<System.Void>`
/// (a call with that type argument) or a value of a type built over it.
let rec private voidUses (e: FSharpExpr) : range list =
    let own =
        match e with
        | FSharpExprPatterns.Call(_, _, _, typeArgs, _) when typeArgs |> List.exists mentionsVoid -> [ e.Range ]
        | _ -> if mentionsVoid e.Type then [ e.Range ] else []
    own @ (e.ImmediateSubExpressions |> List.collect voidUses)

/// Declaration-level members that hold a block and whose body mentions `System.Void` as a type
/// argument: the block(s) and the member, with the first offending use.
let rec private undecodableIn (decls: FSharpImplementationFileDeclaration list) : (range * FSharpMemberOrFunctionOrValue * range) list =
    decls
    |> List.collect (fun decl ->
        match decl with
        | FSharpImplementationFileDeclaration.Entity(_, sub) -> undecodableIn sub
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(mfv, _, body) ->
            match blocksIn body with
            | [] -> []
            | blocks ->
                match voidUses body with
                | [] -> []
                | use' :: _ -> [ for b in blocks -> b.Range, mfv, use' ]
        | FSharpImplementationFileDeclaration.InitAction _ -> [])

let private analyzeUndecodable (typedTree: FSharpImplementationFileContents option) : Message list =
    match typedTree with
    | None -> []
    | Some contents ->
        undecodableIn contents.Declarations
        |> List.map (fun (block, mfv, use') ->
            { Type = "dlr { } in a member whose reflected definition will not decode"
              Message = sprintf "dlr { } inside '%s' fails at run time: the member's reflected definition holds typeof<System.Void> (line %d), which FSharp.Core cannot decode, so no block in it finds its body. Use typeof<unit>, or move the block or the typeof into another function." mfv.DisplayName use'.StartLine
              Code = UndecodableCode
              Severity = Severity.Error
              Range = block
              Fixes = [] })

/// A local value, told apart from a shadowed one of the same name by where it is declared.
let private localKey (v: FSharpMemberOrFunctionOrValue) =
    v.DisplayName, v.DeclarationLocation.StartLine, v.DeclarationLocation.StartColumn

/// The local values `e` reads or assigns that it does not bind itself.
let private freeLocals (e: FSharpExpr) : FSharpMemberOrFunctionOrValue list =
    let read = System.Collections.Generic.Dictionary<string * int * int, FSharpMemberOrFunctionOrValue>()
    let bound = System.Collections.Generic.HashSet<string * int * int>()
    let rec go (e: FSharpExpr) =
        match e with
        | FSharpExprPatterns.Value v
        | FSharpExprPatterns.ValueSet(v, _) when not v.IsModuleValueOrMember -> read.[localKey v] <- v
        | _ -> ()
        match e with
        | FSharpExprPatterns.Let((v, _, _), _) -> bound.Add(localKey v) |> ignore
        | FSharpExprPatterns.LetRec(bindings, _) -> for (v, _, _) in bindings do bound.Add(localKey v) |> ignore
        | FSharpExprPatterns.Lambda(v, _) -> bound.Add(localKey v) |> ignore
        | FSharpExprPatterns.TryWith(_, v1, _, v2, _, _, _) -> bound.Add(localKey v1) |> ignore; bound.Add(localKey v2) |> ignore
        | FSharpExprPatterns.DecisionTree(_, targets) -> for (vs, _) in targets do for v in vs do bound.Add(localKey v) |> ignore
        | _ -> ()
        for x in e.ImmediateSubExpressions do go x
    go e
    [ for KeyValue(k, v) in read do if not (bound.Contains k) then v ]

/// The definitions of the member's local `let`s and `let rec`s, by `localKey`, each with its
/// `let rec` group (empty for a `let`).
let private localDefinitions (body: FSharpExpr) =
    let defs = System.Collections.Generic.Dictionary<string * int * int, FSharpExpr * Set<string * int * int>>()
    let rec go (e: FSharpExpr) =
        match e with
        | FSharpExprPatterns.Let((v, def, _), _) -> defs.[localKey v] <- (def, Set.empty)
        | FSharpExprPatterns.LetRec(bindings, _) ->
            let group = bindings |> List.map (fun (v, _, _) -> localKey v) |> Set.ofList
            for (v, def, _) in bindings do defs.[localKey v] <- (def, group)
        | _ -> ()
        for x in e.ImmediateSubExpressions do go x
    go body
    defs

/// For each block in a member: a name two values it reaches share, and the local function or
/// alias the one not its own is reached through. The translator's `Captures.reached` at build
/// time, taking every local function and alias (`let y = x` of an immutable `x`, a literal) the block reads as
/// inlined (fields unknown): each stands for what its definition reads, and an alias reached
/// that way is substituted, not counted.
let private shadowedIn (body: FSharpExpr) (block: FSharpExpr) : (string * string) list =
    let defs = localDefinitions body
    let own = freeLocals block
    let ownKeys = own |> List.map localKey |> Set.ofList
    let isAlias (e: FSharpExpr) =
        match e with
        | FSharpExprPatterns.Value v -> not v.IsModuleValueOrMember && not v.IsMutable
        | FSharpExprPatterns.Const _ -> true
        | _ -> false
    let isFunction (e: FSharpExpr) = match e with FSharpExprPatterns.Lambda _ -> true | _ -> false
    let seen = System.Collections.Generic.HashSet<string * int * int>()
    let counted = ResizeArray<FSharpMemberOrFunctionOrValue * string option>()
    let rec visit (via: string option) (v: FSharpMemberOrFunctionOrValue) =
        let key = localKey v
        if seen.Add key then
            let def, group = match defs.TryGetValue key with | true, (d, g) -> Some d, g | _ -> None, Set.empty
            let alias = not v.IsMutable && not (ownKeys.Contains key) && (def |> Option.exists isAlias)
            if not alias then counted.Add((v, via))
            match def with
            | Some d when not v.IsMutable && (isAlias d || isFunction d) ->
                // A `let rec` group is one definition: its members' references to each other stay inside it.
                for inner in freeLocals d do
                    if not (group.Contains(localKey inner)) then visit (via |> Option.orElse (Some v.DisplayName)) inner
            | _ -> ()
    for v in own do visit None v
    counted
    |> Seq.groupBy (fun (v, _) -> v.DisplayName)
    |> Seq.choose (fun (name, vs) ->
        if Seq.length vs < 2 then None
        else vs |> Seq.tryPick snd |> Option.map (fun via -> name, via))
    |> List.ofSeq

let private analyzeShadowed (typedTree: FSharpImplementationFileContents option) : Message list =
    match typedTree with
    | None -> []
    | Some contents ->
        let rec decls (ds: FSharpImplementationFileDeclaration list) =
            ds |> List.collect (function
                | FSharpImplementationFileDeclaration.Entity(_, sub) -> decls sub
                | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(_, _, body) ->
                    [ for b in blocksIn body do for found in shadowedIn body b -> b.Range, found ]
                | FSharpImplementationFileDeclaration.InitAction _ -> [])
        decls contents.Declarations
        |> List.map (fun (r, (name, via)) ->
            { Type = "dlr { } reaches two values of one name"
              Message = sprintf "dlr { } reaches two values named '%s', one through '%s'. Where the Release optimizer inlines '%s', the block cannot tell them apart and its first call raises DlrTranslationException. Rename one." name via via
              Code = ShadowedCode
              Severity = Severity.Warning
              Range = r
              Fixes = [] })

/// Whether a module function is left out of the signature file: an enclosing module has one (its
/// signature location is in a `.fsi`), the function's own signature location is its declaration.
/// Only what compiles to a method (a function, a type function) is erased once inlined; a hidden
/// constant or table is a property the optimizer keeps.
let private hiddenBySignature (v: FSharpMemberOrFunctionOrValue) =
    let inSignature (r: FSharp.Compiler.Text.range option) = r |> Option.exists (fun r -> r.FileName.EndsWith ".fsi")
    let rec signedModule (e: FSharpEntity option) =
        match e with
        | Some e when e.IsFSharpModule -> inSignature e.SignatureLocation || signedModule e.DeclaringEntity
        | _ -> false
    try
        v.IsModuleValueOrMember
        && not v.IsMember
        && (v.FullType.IsFunctionType || v.GenericParameters.Count > 0)
        && not (inSignature v.SignatureLocation)
        && signedModule v.DeclaringEntity
        && not (v.Attributes |> Seq.exists (fun a ->
                    a.AttributeType.TryFullName = Some "System.Runtime.CompilerServices.MethodImplAttribute"
                    && a.ConstructorArguments |> Seq.exists (fun (_, arg) ->
                        match arg with
                        | :? int as flags -> flags &&& 8 <> 0   // MethodImplOptions.NoInlining
                        | o -> (string o).Contains "NoInlining")))
    with _ -> false

/// Each reflected definition's calls and reads of values its module's signature hides: the call
/// and the value.
let rec private hiddenInReflected (reflected: bool) (decls: FSharpImplementationFileDeclaration list) : (range * FSharpMemberOrFunctionOrValue * FSharpMemberOrFunctionOrValue) list =
    decls
    |> List.collect (fun decl ->
        match decl with
        | FSharpImplementationFileDeclaration.Entity(entity, sub) -> hiddenInReflected (reflected || isReflectedDefinition entity.Attributes) sub
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(mfv, _, body) when reflected || memberIsReflected mfv ->
            let rec uses (e: FSharpExpr) =
                [ match e with
                  | FSharpExprPatterns.Call(_, v, _, _, _) | FSharpExprPatterns.Value v when hiddenBySignature v -> yield e.Range, mfv, v
                  | _ -> ()
                  for x in e.ImmediateSubExpressions do yield! uses x ]
            uses body
        | _ -> [])

let private analyzeHiddenBySignature (typedTree: FSharpImplementationFileContents option) : Message list =
    match typedTree with
    | None -> []
    | Some contents ->
        hiddenInReflected false contents.Declarations
        |> List.distinctBy (fun (r, _, v) -> r.StartLine, r.StartColumn, v.DisplayName)
        |> List.map (fun (r, owner, v) ->
            { Type = "reflected definition calls a value the signature hides"
              Message =
                sprintf "'%s' has a reflected definition that uses '%s', which the signature file leaves out. Where the Release optimizer inlines '%s' it erases it, and FSharp.Core then cannot decode any of this assembly's reflected definitions: every dlr { } in the assembly fails. Add '%s' to the signature, or mark it [<MethodImpl(MethodImplOptions.NoInlining)>]." owner.DisplayName v.DisplayName v.DisplayName v.DisplayName
              Code = HiddenBySignatureCode
              Severity = Severity.Warning
              Range = r
              Fixes = [] })

let private analyze (tree: ParsedInput) (typedTree: FSharpImplementationFileContents option) : Message list =
    let inline' = analyzeInline typedTree
    // A block DLR004 refuses gets no DLR001 as well: the add-the-attribute fix would not help it.
    let refused = inline' |> List.map (fun m -> m.Range)
    analyzeOutside typedTree
    @ analyzeSharedLines typedTree
    @ inline'
    @ analyzeArgumentMarkers typedTree
    @ analyzeUndecodable typedTree
    @ analyzeShadowed typedTree
    @ analyzeHiddenBySignature typedTree
    @ match typedTree with
      | None -> []
      | Some contents ->
        findInDeclarations false contents.Declarations
        |> List.filter (fun finding -> not (refused |> List.exists (fun r -> Range.equals r finding.Block)))
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
