namespace FSharp.Interop.Dlr

open System
open System.Linq.Expressions
open FSharp.Quotations
open FSharp.Quotations.Patterns
open FSharp.Quotations.DerivedPatterns
open FSharp.Quotations.ExprShape
open FSharp.Reflection
open Microsoft.FSharp.Linq.RuntimeHelpers
open System.Runtime.CompilerServices

module internal TranslateMembers =
    open TranslatePatterns
    open SiteHoisting
    open TranslateBlock

    /// The marker operations: a literal name is a baked site; a computed name or runtime type
    /// arguments bind per key through a SiteCache.
    module Members =

        let private targetArg (rewriteIn: Rewrite) bound (target: Expr) =
            match target with
            | StaticTarget t -> Binders.staticTarget t
            | _ ->
                let t = rewriteIn bound target
                Binders.dynamicArg (if t.Type = typeof<obj> then t else Expr.Coerce(t, typeof<obj>))

        let private valueArg (rewriteIn: Rewrite) bound (value: Expr) =
            match value with
            | Value _ -> Binders.constant (Binders.typedArg value)
            // A unit-valued expression (a void call, `ignore x`) as a value: run, then `()`.
            | _ when value.Type = typeof<unit> -> Binders.typedArg (Expr.Sequential(rewriteIn bound value, Expr.Value(())))
            | _ -> Binders.typedArg (rewriteIn bound value)

        let private argList (rewriteIn: Rewrite) bound (argExprs: Expr list) =
            let bindings = ResizeArray()
            let args =
                [ for a in argExprs do
                    match a with
                    | TypeArgs _ -> unsupported "Dlr.typeArgs anywhere but as the first argument of a member call (a value invoked with Dlr.call / Dlr.apply or a constructor takes no type arguments)" a
                    | NamedOf _ | ArgsOf _ -> unsupported "Dlr.namedOf / Dlr.argsOf here (they go in the arguments of a member call, Dlr.call / Dlr.apply, or Dlr.new')" a
                    | OutMarker _ | RefMarker _ -> unsupported "Dlr.out / Dlr.outAs / Dlr.ref here: they go directly in the arguments of a call (x?M(…), Dlr.get, Dlr.invoke, Dlr.call / Dlr.apply; Dlr.ref in Dlr.new'), not beside Dlr.namedOf / Dlr.argsOf" a
                    | NamedRecord(lets, fields) ->
                        bindings.AddRange lets
                        let inner = bindings |> Seq.fold (fun (b: Set<Var>) (v, _) -> b.Add v) bound
                        for (name, v) in fields -> Binders.named name (valueArg rewriteIn inner v)
                    // Argument positions are generic-typed, so a `Coerce(_, obj)` here is the user's
                    // `x :> obj` and means what `box x` means: an obj argument, runtime dispatch.
                    | v -> yield valueArg rewriteIn bound v ]
            List.ofSeq bindings, args

        let private bind (rewriteIn: Rewrite) bound (bindings: (Var * Expr) list) (call: Expr) =
            List.foldBack (fun (v, value) body -> Expr.Let(v, rewriteIn bound value, body)) bindings call

        let private indexList (rewriteIn: Rewrite) bound (indexes: Expr list) = [ for i in indexes -> valueArg rewriteIn bound i ]

        let private finish (block: Block) discard (resultType: Type) (call: Expr) =
            if discard then call else block.Convert resultType call

        /// A call with `Dlr.out` / `Dlr.ref` arguments (#131). The outs come back as F# returns a
        /// method's out parameters: the result is a tuple of the return value then each out in
        /// order, or — when the result has no slot for the return value, which is then discarded —
        /// the outs alone (the bare value for one); each out's type is its element of the result
        /// type. A ref's value is read from its `let mutable` at the call, after every other argument
        /// (the callers bind those in order: `sequenced`), and the method's write is assigned back.
        /// Through `Binders.ByRefSite`, whose holder `makeCall` returns and is unpacked here.
        let private byRefCall (rewriteIn: Rewrite) bound (argExprs: Expr list) (resultType: Type) (convertReturn: Type -> Expr -> Expr) (makeCall: bool -> Binders.Arg list -> Expr) : Expr =
            // Each out's stated type (`Dlr.outAs<'T>`), or None to take it from the result's shape.
            let stated = argExprs |> List.choose (function OutMarker t -> Some t | _ -> None)
            let outCount = stated.Length
            let shapeError () =
                raise (DlrTranslationException(sprintf "dlr { } does not support a call with %d Dlr.out argument(s) whose result is %s: the result is the return value then each out as a tuple (reference or struct), the outs alone as a tuple of two or more, or the one out's value — the first of these that agrees with every type Dlr.outAs states" outCount resultType.Name))
            let agrees (outTypes: Type list) = List.forall2 (fun s t -> match s with Some s -> s = t | None -> true) stated outTypes
            // The shapes in order, the first that agrees with the stated types: the return value then
            // the outs; the outs alone (two or more); the one out's bare value — for a tuple-typed
            // result only when Dlr.outAs states that type, so a plain Dlr.out keeps a mismatched
            // tuple a shape error (the analyzer's too). A reference or a struct tuple: `let struct
            // (ok, v) = …` allocates no tuple.
            let returnType, outTypes =
                if outCount = 0 then (if resultType = typeof<unit> then None else Some resultType), []
                elif resultType = typeof<unit> then shapeError ()
                else
                    let elements = if FSharpType.IsTuple resultType then List.ofArray (FSharpType.GetTupleElements resultType) else []
                    if elements.Length = outCount + 1 && agrees elements.Tail then Some elements.Head, elements.Tail
                    elif outCount >= 2 && elements.Length = outCount && agrees elements then None, elements
                    elif outCount = 1 && (elements.IsEmpty || stated = [ Some resultType ]) && agrees [ resultType ] then None, [ resultType ]
                    else shapeError ()
            let pendingOuts = System.Collections.Generic.Queue<Type>(outTypes)
            // Per byref argument, in order: an out's type, or a ref's place (its type and how to
            // write it back).
            let byRefs = ResizeArray<Choice<Type, Type * (Expr -> Expr)>>()
            let namedBindings = ResizeArray<Var * Expr>()
            let args =
                [ for a in argExprs do
                    match a with
                    | OutMarker _ ->
                        let t = pendingOuts.Dequeue()
                        byRefs.Add(Choice1Of2 t)
                        yield Binders.byRefArg true t (Expr.Value(null, typeof<obj>)) null
                    | RefMarker(Var v) when v.IsMutable ->
                        byRefs.Add(Choice2Of2(v.Type, fun value -> Expr.VarSet(v, value)))
                        yield Binders.byRefArg false v.Type (rewriteIn bound (Expr.Var v)) (box v)
                    // A `let mutable` a closure captures is a ref cell by the time it is quoted.
                    | RefMarker(PropertyGet(Some(Var cell as cellExpr), p, [])) when cell.Type.IsGenericType && cell.Type.GetGenericTypeDefinition() = typedefof<Ref<_>> ->
                        byRefs.Add(Choice2Of2(p.PropertyType, fun value -> Expr.PropertySet(cellExpr, p, value)))
                        yield Binders.byRefArg false p.PropertyType (rewriteIn bound (Expr.PropertyGet(cellExpr, p))) (box cell)
                    | RefMarker other -> unsupported "Dlr.ref of anything but a let mutable (its value goes in, and the method's write is assigned back to it)" other
                    // Named arguments, as `argList` takes them: the record's field temporaries
                    // wrap the call, in source order.
                    | NamedRecord(lets, fields) ->
                        namedBindings.AddRange lets
                        let inner = namedBindings |> Seq.fold (fun (b: Set<Var>) (v, _) -> b.Add v) bound
                        for (name, v) in fields -> Binders.named name (valueArg rewriteIn inner v)
                    | NamedOf _ | ArgsOf _ -> unsupported "Dlr.namedOf / Dlr.argsOf in a call with Dlr.out, Dlr.outAs or Dlr.ref (not supported: the outs' types are fixed by the result, the splat's arity is not)" a
                    | TypeArgs _ -> unsupported "Dlr.typeArgs anywhere but as the first argument of a member call" a
                    | v -> yield valueArg rewriteIn bound v ]
            let call = makeCall returnType.IsNone args
            // The holder (Binders.byRefHolderType): the result as obj, then each byref's value, typed.
            let results = Var("byRefResults", call.Type)
            let at (i: int) = Binders.byRefHolderPath call.Type i |> List.fold (fun (e: Expr) f -> Expr.FieldGet(e, f)) (Expr.Var results)
            let bound' = Set.add results bound
            let writeBacks =
                [ for i, b in Seq.indexed byRefs do
                    match b with
                    | Choice2Of2(_, write) -> yield rewriteIn bound' (write (at (i + 1)))
                    | Choice1Of2 _ -> () ]
            let outValues = [ for i, b in Seq.indexed byRefs do match b with Choice1Of2 _ -> yield at (i + 1) | Choice2Of2 _ -> () ]
            // The result tuple, of the result type's kind: a struct one is the ValueTuple constructor.
            let newTuple (values: Expr list) = if resultType.IsValueType then Tuples.newQuotation resultType values else Expr.NewTuple values
            let value =
                match returnType, outValues with
                | None, [] -> Expr.Value(())
                | Some t, [] -> convertReturn t (at 0)
                | Some t, outs -> newTuple (convertReturn t (at 0) :: outs)
                | None, [ single ] -> single
                | None, outs -> newTuple outs
            let body = List.foldBack (fun w rest -> Expr.Sequential(w, rest)) writeBacks value
            List.foldBack (fun (v, value) body -> Expr.Let(v, rewriteIn bound value, body)) (List.ofSeq namedBindings) (Expr.Let(results, call, body))

        /// The tuple bindings of `splitArgs`, added to the bound set for the argument rewrites.
        let private withTuple (bound: Set<Var>) (tupleBindings: (Var * Expr) list) =
            tupleBindings |> List.fold (fun (b: Set<Var>) (v, _) -> b.Add v) bound

        /// The name and the type arguments of a keyed site, each either static (a constant in the
        /// key) or an expression evaluated per call in the scope the site is emitted in.
        type KeySpec = Choice<string, Expr> * Choice<Type list, Expr>

        /// A delegate over `parameters` returning obj, compiled once per site: a `Func<…>` while
        /// its arity allows; past that the delegate type would be emitted at run time, which a
        /// quotation must not name (see `Binders.WideSite`), so it is a `Func<obj[], obj>` over the
        /// parameters packed (`packArguments` at the call) and unpacked to their types inside.
        let private isWide (parameters: Var list) = not (DelegateMembers.funcFits parameters.Length)
        let private delegateTypeOver (parameters: Var list) =
            if isWide parameters then typeof<Func<obj[], obj>>
            else Expression.GetDelegateType(Array.ofList ([ for v in parameters -> v.Type ] @ [ typeof<obj> ]))
        let private lambdaOver (parameters: Var list) (body: Expr) =
            if not (isWide parameters) then Expr.NewDelegate(delegateTypeOver parameters, parameters, body)
            else
                let packed = Var("packed", typeof<obj[]>)
                let atArg = typeof<NamedOfCache>.GetMethod("At")
                let unpacked =
                    List.foldBack (fun (i, v: Var) (inner: Expr) ->
                        let element = Expr.Call(atArg, [ Expr.Var packed; Expr.Value i ])
                        Expr.Let(v, (if v.Type = typeof<obj> then element else Expr.Coerce(element, v.Type)), inner))
                        (List.indexed parameters) body
                Expr.NewDelegate(typeof<Func<obj[], obj>>, [ packed ], unpacked)
        let private packArguments (parameters: Var list) (arguments: Expr list) =
            if not (isWide parameters) then arguments
            else [ Expr.NewArray(typeof<obj>, [ for a in arguments -> if a.Type = typeof<obj> then a else Expr.Coerce(a, typeof<obj>) ]) ]

        /// An operation whose binder inputs — the member name, the type arguments, or both — are
        /// only known at run time: the operation's delegate is compiled once here with its call
        /// sites as parameters (lifted from a template built for a placeholder key), and a
        /// SiteCache constant creates the sites per distinct `(name, types)` key; the emitted
        /// code is `let sites = cache.Get((name, types)) in delegate.Invoke(sites.[0], …, target, args…)`.
        /// Argument names in `Dlr.named` stay static. This is the core over prepared arguments;
        /// the name and type expressions must be valid where the result is placed.
        let private keyedSiteTyped (key: KeySpec) (targetInfo: Binders.Arg) (argInfos: Binders.Arg list) (site: string -> Type list -> Binders.Arg -> Binders.Arg list -> Expr) : Expr * Type =
            let targetVar = Var("target", targetInfo.Type)
            // A byref argument (`Dlr.out` / `Dlr.ref`) is passed as its value: a quotation variable cannot be a byref.
            let argVars = argInfos |> List.mapi (fun i a -> Var(sprintf "a%d" i, (if a.Type.IsByRef then a.Expr.Type else a.Type)))
            let template (name: string, types: Type list) =
                let args = List.map2 (fun (info: Binders.Arg) (v: Var) -> { info with Expr = Expr.Var v }) argInfos argVars
                site name types { targetInfo with Expr = Expr.Var targetVar } args
            // The operation's shape does not depend on the key, only its sites do: build it once
            // for a placeholder, lift every site constant into a parameter, and compile that one
            // delegate now. Per key, the cache creates the sites and hands them back in the
            // same order.
            let placeholderName, nameE =
                match fst key with
                | Choice1Of2 name -> name, Expr.Value name
                | Choice2Of2 e -> "name", e
            let placeholderTypes, typesE =
                match snd key with
                | Choice1Of2 ts -> ts, Expr.Value(ts, typeof<Type list>)
                | Choice2Of2 e -> [], e
            let placeholder = template (placeholderName, placeholderTypes)
            let sites = SiteCache<string * Type list>.Sites placeholder
            // A wide site's type names an emitted delegate type: keep it out of the quotation (see
            // `Binders.WideSite`) by typing the parameter as the base `CallSite`.
            let siteVars = sites |> List.mapi (fun i s -> s, Var(sprintf "site%d" i, (let t = s.GetType() in if t.GetGenericArguments().[0].Assembly.IsDynamic then typeof<CallSite> else t)))
            let rec lift (e: Expr) =
                match e with
                | Value(v, _) when (v :? CallSite) ->
                    match siteVars |> List.tryFind (fun (s, _) -> obj.ReferenceEquals(s, v)) with
                    | Some(_, var) -> Expr.Var var
                    | None -> e
                | _ -> Quotation.rebuild lift e
            let body = lift placeholder
            // A discarded result is a void site: the delegate still returns obj, so hand back null.
            let boxed =
                if body.Type = typeof<obj> then body
                elif body.Type = typeof<unit> || body.Type = typeof<Void> then Expr.Sequential(body, Expr.Value(null, typeof<obj>))
                else Expr.Coerce(body, typeof<obj>)
            let parameters = [ for _, v in siteVars -> v ] @ targetVar :: argVars
            let cache = SiteCache<string * Type list>(template)
            let cacheType = typeof<SiteCache<string * Type list>>
            let sitesVar = Var("sites", typeof<CallSite[]>)
            let at = cacheType.GetMethod("At")
            let siteArgs = siteVars |> List.mapi (fun i (_, v) -> Expr.Coerce(Expr.Call(at, [ Expr.Var sitesVar; Expr.Value i ]), v.Type))
            let arguments = siteArgs @ targetInfo.Expr :: [ for a in argInfos -> a.Expr ]
            let siteDelegate = delegateTypeOver parameters
            // Through the hoister like every compiled tree: a wide site's placeholder in the template
            // is rewritten there (its site arrives as a parameter, which the rewrite converts).
            let linq = LeafExpressionConverter.QuotationToExpression (lambdaOver parameters boxed) :?> LambdaExpression
            let compiled = (SiteHoister().Visit linq :?> LambdaExpression).Compile()
            let invocation = Expr.Call(Expr.Value(compiled, siteDelegate), siteDelegate.GetMethod("Invoke"), packArguments parameters arguments)
            Expr.Let(sitesVar, Expr.Call(Expr.Value(cache, cacheType), cacheType.GetMethod("Get"), [ Expr.NewTuple [ nameE; typesE ] ]), invocation),
            placeholder.Type

        /// The keyed call, boxed to `obj` whatever the operation's own type.
        let private keyedSiteRaw key targetInfo argInfos site : Expr = fst (keyedSiteTyped key targetInfo argInfos site)

        /// `keyedSiteRaw`'s call converted to `resultType`: cast back where the operation already is
        /// of that type (a member read as a function or delegate type), converted otherwise (a
        /// call's result, which may be a delegate typed as a function or back, #207).
        let private keyedSiteCore (block: Block) (key: KeySpec) (targetInfo: Binders.Arg) (argInfos: Binders.Arg list) (resultType: Type) (site: string -> Type list -> Binders.Arg -> Binders.Arg list -> Expr) : Expr =
            let call, operationType = keyedSiteTyped key targetInfo argInfos site
            if operationType = resultType then Expr.Coerce(call, resultType) else block.Convert resultType call

        /// The key of a computed name / run-time type arguments, its expressions rewritten in
        /// the block's scope.
        let private keySpec (rewriteIn: Rewrite) bound (nameExpr: Expr) (typeArgs: TypeArgsSpec) : KeySpec =
            (match nameExpr with Literal name -> Choice1Of2(string name) | e -> Choice2Of2(rewriteIn bound e)),
            (match typeArgs with StaticTypes ts -> Choice1Of2 ts | RuntimeTypes e -> Choice2Of2(rewriteIn bound e))

        /// `keyedSiteCore` over argument expressions, in the block's scope.
        let private keyedSite (block: Block) (rewriteIn: Rewrite) bound (nameExpr: Expr) (typeArgs: TypeArgsSpec) (target: Expr) (argExprs: Expr list) (resultType: Type) (site: string -> Type list -> Binders.Arg -> Binders.Arg list -> Expr) : Expr =
            let bindings, argInfos = argList rewriteIn bound argExprs
            keyedSiteCore block (keySpec rewriteIn bound nameExpr typeArgs) (targetArg rewriteIn bound target) argInfos resultType site
            |> bind rewriteIn bound bindings

        /// An invocation whose arguments include `Dlr.namedOf pairs`: the operation is compiled
        /// per distinct name list — the names decide the site's arity, so the whole delegate is
        /// per key — through a bounded `NamedOfCache` constant, taking the target, the fixed
        /// arguments and the named values (`obj`, dispatched on runtime type). `operation` builds
        /// the site expression for a target and its full argument list. `None` when there is no
        /// `namedOf` in `argExprs`. With a computed member name / run-time type arguments
        /// (`key`), their expressions are evaluated in the block's scope and passed into the
        /// per-name-list delegate as parameters, where `operation` gets them for a `keyedSiteCore`.
        let private namedOfCallKeyed (block: Block) (rewriteIn: Rewrite) bound (target: Expr) (argExprs: Expr list) (resultType: Type) (discard: bool) (key: KeySpec option) (operation: KeySpec -> Binders.Arg -> Binders.Arg list -> Expr) : Expr option =
            let namedOfs = argExprs |> List.choose (function NamedOf pairs -> Some pairs | _ -> None)
            let argsOfs = argExprs |> List.choose (function ArgsOf values -> Some values | _ -> None)
            if namedOfs.IsEmpty && argsOfs.IsEmpty then None
            else
                if namedOfs.Length > 1 then unsupported "more than one Dlr.namedOf in one call (concatenate the lists)" namedOfs.[1]
                if argsOfs.Length > 1 then unsupported "more than one Dlr.argsOf in one call (concatenate the lists)" argsOfs.[1]
                // Named arguments are the call's trailing ones (the binder's CallInfo names the
                // last arguments), so nothing positional may follow Dlr.namedOf.
                (match argExprs |> List.tryFindIndex (function NamedOf _ -> true | _ -> false) with
                 | Some i when argExprs |> List.skip (i + 1) |> List.exists (function NamedRecord _ -> false | _ -> true) ->
                    unsupported "a positional argument after Dlr.namedOf (named arguments come last)" argExprs.[i]
                 | _ -> ())
                let pairsExpr = match namedOfs with [ p ] -> p | _ -> Expr.Value(([]: (string * obj) list), typeof<(string * obj) list>)
                let positionalExpr = match argsOfs with [ p ] -> p | _ -> Expr.Value(([]: obj list), typeof<obj list>)
                /// The argument list's layout: fixed arguments by index, the positional splat, the
                /// named splat — so the compiled call keeps the source order.
                let layout =
                    let mutable next = 0
                    argExprs |> List.choose (fun a ->
                        match a with
                        | NamedOf _ -> Some(Choice3Of3 ())
                        | ArgsOf _ -> Some(Choice2Of3 ())
                        | _ -> (let i = next in next <- next + 1; Some(Choice1Of3 i)))
                let fixedExprs = argExprs |> List.filter (fun a -> not (isSplat a))
                let bindings, fixedInfos = argList rewriteIn bound fixedExprs
                let targetInfo = targetArg rewriteIn bound target
                let targetVar = Var("target", targetInfo.Type)
                let fixedVars = fixedInfos |> List.mapi (fun i a -> Var(sprintf "a%d" i, a.Type))
                let valuesVar = Var("values", typeof<obj[]>)
                let at = typeof<NamedOfCache>.GetMethod("At")
                // A computed name / run-time type list are evaluated out here and passed in as
                // parameters: the delegate does not see the block's scope.
                let keyVars, keyExprs, innerKey =
                    match key with
                    | None -> [], [], None
                    | Some(name, types) ->
                        let nameVar, nameE = (match name with Choice1Of2 n -> None, Choice1Of2 n | Choice2Of2 e -> (let v = Var("name", typeof<string>) in Some(v, e), Choice2Of2(Expr.Var v)))
                        let typesVar, typesE = (match types with Choice1Of2 ts -> None, Choice1Of2 ts | Choice2Of2 e -> (let v = Var("types", typeof<Type list>) in Some(v, e), Choice2Of2(Expr.Var v)))
                        let vars = [ nameVar; typesVar ] |> List.choose id
                        List.map fst vars, List.map snd vars, Some((nameE, typesE): KeySpec)
                // One delegate type for every key at this site: the names change the sites inside,
                // not the parameters, so the call is a typed Invoke, not DynamicInvoke.
                let parameters = targetVar :: fixedVars @ keyVars @ [ valuesVar ]
                let siteDelegate = delegateTypeOver parameters
                let compile (names: string list) : Delegate =
                    let fixed' = List.map2 (fun (info: Binders.Arg) (v: Var) -> { info with Expr = Expr.Var v }) fixedInfos fixedVars
                    // `fixedInfos` are grouped per source argument (a Dlr.named record is several):
                    // walk the fixed expressions again to know how many each contributed.
                    let fixedGroups =
                        let counted = fixedExprs |> List.map (fun a -> match a with NamedRecord(_, fields) -> fields.Length | _ -> 1)
                        let mutable offset = 0
                        [ for n in counted -> (let g = List.take n (List.skip offset fixed') in offset <- offset + n; g) ]
                    let value i = Binders.dynamicArg (Expr.Call(at, [ Expr.Var valuesVar; Expr.Value i ]))
                    let positionalCount = names |> List.takeWhile (fun n -> n.Length = 0) |> List.length
                    let positional = [ for i in 0 .. positionalCount - 1 -> value i ]
                    let named = names |> List.skip positionalCount |> List.mapi (fun j name -> Binders.named name (value (positionalCount + j)))
                    let args =
                        layout |> List.collect (function
                            | Choice1Of3 i -> fixedGroups.[i]
                            | Choice2Of3 () -> positional
                            | Choice3Of3 () -> named)
                    let body = operation (defaultArg innerKey (Choice1Of2 "", Choice1Of2 [])) { targetInfo with Expr = Expr.Var targetVar } args
                    let boxed =
                        if body.Type = typeof<obj> then body
                        elif body.Type = typeof<unit> || body.Type = typeof<Void> then Expr.Sequential(body, Expr.Value(null, typeof<obj>))
                        else Expr.Coerce(body, typeof<obj>)
                    let linq = LeafExpressionConverter.QuotationToExpression (lambdaOver parameters boxed) :?> LambdaExpression
                    (SiteHoister().Visit linq :?> LambdaExpression).Compile()
                let cache = NamedOfCache(compile)
                let cacheType = typeof<NamedOfCache>
                let pairsVar = Var("pairs", typeof<(string * obj) list>)
                let positionalVar = Var("positional", typeof<obj list>)
                let delegateVar = Var("d", siteDelegate)
                let values = Expr.Call(cacheType.GetMethod("Values"), [ Expr.Var positionalVar; Expr.Var pairsVar ])
                let call =
                    Expr.Let(positionalVar, rewriteIn bound positionalExpr,
                      Expr.Let(pairsVar, rewriteIn bound pairsExpr,
                        Expr.Let(delegateVar, Expr.Coerce(Expr.Call(Expr.Value(cache, cacheType), cacheType.GetMethod("Get"), [ Expr.Var positionalVar; Expr.Var pairsVar ]), siteDelegate),
                            Expr.Call(Expr.Var delegateVar, siteDelegate.GetMethod("Invoke"), packArguments parameters (targetInfo.Expr :: [ for a in fixedInfos -> a.Expr ] @ keyExprs @ [ values ])))))
                (if discard then Expr.Sequential(call, Expr.Value(())) else block.Convert resultType call)
                |> bind rewriteIn bound bindings
                |> Some

        /// An invocation whose arguments include `Dlr.namedOf pairs`, with a literal member name.
        let private namedOfCall (block: Block) (rewriteIn: Rewrite) bound (target: Expr) (argExprs: Expr list) (resultType: Type) (discard: bool) (operation: Binders.Arg -> Binders.Arg list -> Expr) : Expr option =
            namedOfCallKeyed block rewriteIn bound target argExprs resultType discard None (fun _ t args -> operation t args)

        /// An expression typed as a delegate type a member can be read as (#201), with the function
        /// type its invoker takes past fourteen parameters (`Binders.delegateRead`).
        let private (|DelegateRead|_|) (e: Expr) = Binders.delegateRead e.Type

        /// `(?) x name` with a computed name (no type arguments): see keyedSite.
        let private computedName block rewriteIn bound (nameExpr: Expr) (target: Expr) (argExprs: Expr list) (resultType: Type) (site: string -> Binders.Arg -> Binders.Arg list -> Expr) : Expr =
            keyedSite block rewriteIn bound nameExpr (StaticTypes []) target argExprs resultType (fun name _ targetArg args -> site name targetArg args)

        /// `Dlr.addAssign`/`subtractAssign`: bind the target and value once, then both branches
        /// of the IsEvent decision refer to them. A computed name goes through the SiteCache
        /// like any other member operation.
        let private compoundAssign (block: Block) (rewriteIn: Rewrite) bound (subtract: bool) (nameExpr: Expr) (target: Expr) (value: Expr) : Expr =
            match nameExpr with
            | Literal name ->
                let targetInfo = targetArg rewriteIn bound target
                let v = rewriteIn bound value
                let tv = Var("target", targetInfo.Type)
                let vv = Var("value", v.Type)
                // A literal value keeps C#'s constant conversions (a byte member += 1) even though
                // it is read through a variable here.
                let valueArg =
                    let a = Binders.typedArg (Expr.Var vv)
                    match value with
                    | Value _ -> Binders.constant a
                    | _ -> a
                let body = Binders.compoundAssign block.Context (string name) subtract { targetInfo with Expr = Expr.Var tv } valueArg
                Expr.Let(tv, targetInfo.Expr, Expr.Let(vv, v, body))
            | _ ->
                computedName block rewriteIn bound nameExpr target [ value ] typeof<unit> (fun name targetArg args ->
                    // The name-cache template already makes target and value delegate parameters.
                    Expr.Sequential(Binders.compoundAssign block.Context name subtract targetArg (List.head args), Expr.Value(null, typeof<obj>)))

        /// The expression, if it is a marker operation.
        let tryOperation (block: Block) (rewriteIn: Rewrite) (bound: Set<Var>) (e: Expr) : Expr option =
            let context = block.Context
            let convert = block.Convert
            let rewrite = rewriteIn bound
            let targetArg = targetArg rewriteIn
            let valueArg = valueArg rewriteIn
            let argList = argList rewriteIn
            let bind = bind rewriteIn
            let indexList = indexList rewriteIn
            let finish = finish block
            let computedName = computedName block rewriteIn bound
            match e with
            | MemberOp(InvokeMember(target, nameExpr, argExpr)) when (snd (splitArgs argExpr)) |> List.exists isByRefMarker ->
                let tupleBindings, argExprs = splitArgs argExpr
                if not tupleBindings.IsEmpty then unsupported "Dlr.out / Dlr.outAs / Dlr.ref in a tuple held in a variable" argExpr
                let typeArgs, argExprs =
                    match argExprs with
                    | TypeArgs spec :: rest -> spec, rest
                    | args -> StaticTypes [], args
                // C#'s order, always: the target, a computed name / type list, then the arguments,
                // each impure one bound in turn (a Dlr.named record's temporaries at its place).
                let keys = [ (match nameExpr with Literal _ -> None | k -> Some k); (match typeArgs with RuntimeTypes k -> Some k | _ -> None) ] |> List.choose id
                let bindings, vars, target', keys', argExprs' = sequenced (Some target) keys [] argExprs
                let bound' = Set.union bound vars
                let nameExpr', keys' = (match nameExpr with Literal _ -> nameExpr, keys' | _ -> List.head keys', List.tail keys')
                let typeArgs' = (match typeArgs with RuntimeTypes _ -> RuntimeTypes(List.head keys') | t -> t)
                let targetInfo = targetArg bound' target'.Value
                let makeCall =
                    match nameExpr', typeArgs' with
                    | Literal name, StaticTypes ts -> fun discard args -> Binders.invokeMemberByRef context (string name) ts discard (targetInfo :: args)
                    // A computed name or run-time type arguments: a site per key, as for any member
                    // call; the holder comes back as it is, for byRefCall to unpack.
                    | _ ->
                        let key = keySpec rewriteIn bound' nameExpr' typeArgs'
                        fun discard args ->
                            let holder = Binders.byRefHolderOf (targetInfo :: args)
                            Expr.Coerce(keyedSiteRaw key targetInfo args (fun name ts t a -> Binders.invokeMemberByRef context name ts discard (t :: a)), holder)
                byRefCall rewriteIn bound' argExprs' e.Type convert makeCall |> bind bound bindings |> Some
            | MemberOp(InvokeMember(target, nameExpr, argExpr)) ->
                let tupleBindings, argExprs = splitArgs argExpr
                let typeArgs, argExprs =
                    match argExprs with
                    | TypeArgs spec :: rest -> spec, rest
                    | args -> StaticTypes [], args
                // A computed name / type list is evaluated by the keyed site ahead of the call,
                // so it too is sequenced when impure.
                let keys = [ (match nameExpr with Literal _ -> None | k -> Some k); (match typeArgs with RuntimeTypes k -> Some k | _ -> None) ] |> List.choose id
                let ordered = hoists tupleBindings argExprs || keys |> List.exists (fun k -> not (isPure k))
                let bindings, bound', target, nameExpr, typeArgs, argExprs =
                    if ordered then
                        let bindings, vars, target', keys', argExprs' = sequenced (Some target) keys tupleBindings argExprs
                        let nameExpr', keys' = (match nameExpr with Literal _ -> nameExpr, keys' | _ -> List.head keys', List.tail keys')
                        let typeArgs' = (match typeArgs with RuntimeTypes _ -> RuntimeTypes(List.head keys') | t -> t)
                        bindings, Set.union bound vars, target'.Value, nameExpr', typeArgs', argExprs'
                    else tupleBindings, withTuple bound tupleBindings, target, nameExpr, typeArgs, argExprs
                let discard = e.Type = typeof<unit>
                let hasNamedOf = argExprs |> List.exists isSplat
                match nameExpr, typeArgs with
                | Literal name, StaticTypes ts when hasNamedOf ->
                    (namedOfCall block rewriteIn bound' target argExprs e.Type discard (fun t args -> Binders.invokeMemberOrApply context (string name) ts discard t args)).Value
                | Literal name, StaticTypes ts ->
                    let argBindings, args = argList bound' argExprs
                    Binders.invokeMemberOrApply context (string name) ts discard (targetArg bound' target) args |> finish discard e.Type |> bind bound' argBindings
                | _ when hasNamedOf ->
                    (namedOfCallKeyed block rewriteIn bound' target argExprs e.Type discard (Some(keySpec rewriteIn bound' nameExpr typeArgs)) (fun key t args ->
                        keyedSiteCore block key t args typeof<obj> (fun name ts targetArg args -> Binders.invokeMemberOrApply context name ts discard targetArg args))).Value
                | _ ->
                    keyedSite block rewriteIn bound' nameExpr typeArgs target argExprs e.Type (fun name ts targetArg args ->
                        Binders.invokeMemberOrApply context name ts discard targetArg args)
                |> bind bound bindings
                |> Some
            | MemberOp(GetMember(target, nameExpr)) when FSharpType.IsFunction e.Type ->
                // Read as an F# function: a curried invoker of the member (method, delegate or F#
                // function), so `let f: int -> int -> int = dlr { return x?Add }` then `f 1 2`.
                match nameExpr with
                | Literal name -> Binders.functionMember context (string name) e.Type (targetArg bound target)
                | _ -> computedName nameExpr target [] e.Type (fun name targetArg _ -> Binders.functionMember context name e.Type targetArg)
                |> Some
            | MemberOp(GetMember(target, nameExpr)) & DelegateRead functionType ->
                // Read as a delegate type (#201): the member's value, or an invoker of it when it
                // is a method, so `Api.Fold(dlr { return x?Add })` passes `Add` as the `Func`.
                match nameExpr with
                | Literal name -> Binders.delegateMember context (string name) e.Type functionType (targetArg bound target)
                | _ -> computedName nameExpr target [] e.Type (fun name targetArg _ -> Binders.delegateMember context name e.Type functionType targetArg)
                |> Some
            | MemberOp(GetMember(target, nameExpr)) ->
                match nameExpr with
                | Literal name -> Binders.getMember context (string name) (targetArg bound target) |> convert e.Type
                | _ -> computedName nameExpr target [] e.Type (fun name targetArg _ -> Binders.getMember context name targetArg)
                |> Some
            | Op opAddAssign [ nameExpr; Unboxed value; Unboxed target ] -> Some(compoundAssign block rewriteIn bound false nameExpr target value)
            | Op opSubtractAssign [ nameExpr; Unboxed value; Unboxed target ] -> Some(compoundAssign block rewriteIn bound true nameExpr target value)
            | MemberOp(SetMember(target, nameExpr, value)) ->
                match nameExpr with
                | Literal name -> Binders.setMember context (string name) (targetArg bound target) (valueArg bound value) |> convert typeof<unit>
                | _ ->
                    computedName nameExpr target [ value ] typeof<unit> (fun name targetArg args ->
                        Binders.setMember context name targetArg (List.head args))
                |> Some
            | New(_, argExprs) when argExprs |> List.exists (function OutMarker _ -> true | _ -> false) ->
                unsupported "Dlr.out / Dlr.outAs in Dlr.new': its result is the constructed T, with no room for an out value (Dlr.ref writes back to a variable)" e
            | New(t, argExprs) when argExprs |> List.exists isByRefMarker ->
                // The site's result is `T` itself (see below): the return value unboxes to it.
                let convertReturn (rt: Type) (e: Expr) = if rt = t then Expr.Call(unboxTo.MakeGenericMethod t, [ e ]) else convert rt e
                let bindings, vars, _, _, argExprs' = sequenced None [] [] argExprs
                byRefCall rewriteIn (Set.union bound vars) argExprs' e.Type convertReturn (fun _ args -> Binders.invokeConstructorByRef context t args)
                |> bind bound bindings |> Some
            | New(t, argExprs) ->
                // The site is typed `T` itself (the binder types a constructor's result as `T`,
                // which an obj-typed site rejects for a struct), so no Convert. One argument of a
                // tuple type is several, as for a member call: F# picks the one-argument overload
                // for `Dlr.new'<T> args` and coerces the tuple.
                let tupleBindings, argExprs =
                    match argExprs with
                    | [ single ] -> splitArgs single
                    | many -> [], many
                let bindings, bound', argExprs =
                    if hoists tupleBindings argExprs then
                        let bindings, vars, _, _, argExprs' = sequenced None [] tupleBindings argExprs
                        bindings, Set.union bound vars, argExprs'
                    else tupleBindings, withTuple bound tupleBindings, argExprs
                match namedOfCall block rewriteIn bound' (Expr.Value(null, typeof<obj>)) argExprs e.Type false (fun _ args -> Binders.invokeConstructor context t args) with
                | Some call -> call |> bind bound bindings |> Some
                | None ->
                    let argBindings, args = argList bound' argExprs
                    Binders.invokeConstructor context t args |> bind bound' argBindings |> bind bound bindings |> Some
            // The value's `?`: applied, a call; read at a function type, the target as that function.
            | Application(EtaReduced(Op opCall [ Unboxed target ]), argExpr)
            | Op opApply [ argExpr; Unboxed target ] when (snd (splitArgs argExpr)) |> List.exists isByRefMarker ->
                let tupleBindings, argExprs = splitArgs argExpr
                if not tupleBindings.IsEmpty then unsupported "Dlr.out / Dlr.outAs / Dlr.ref in a tuple held in a variable" argExpr
                let bindings, vars, target', _, argExprs' = sequenced (Some target) [] [] argExprs
                let bound' = Set.union bound vars
                let targetInfo = targetArg bound' target'.Value
                byRefCall rewriteIn bound' argExprs' e.Type convert (fun discard args -> Binders.invokeByRef context discard (targetInfo :: args))
                |> bind bound bindings |> Some
            | Application(EtaReduced(Op opCall [ Unboxed target ]), argExpr)
            | Op opApply [ argExpr; Unboxed target ] ->
                let discard = e.Type = typeof<unit>
                let tupleBindings, argExprs = splitArgs argExpr
                let bindings, bound', target, argExprs =
                    if hoists tupleBindings argExprs then
                        let bindings, vars, target', _, argExprs' = sequenced (Some target) [] tupleBindings argExprs
                        bindings, Set.union bound vars, target'.Value, argExprs'
                    else tupleBindings, withTuple bound tupleBindings, target, argExprs
                match namedOfCall block rewriteIn bound' target argExprs e.Type discard (fun t args -> Binders.invokeOrApply context discard t args) with
                | Some call -> call |> bind bound bindings |> Some
                | None ->
                    let argBindings, args = argList bound' argExprs
                    Binders.invokeOrApply context discard (targetArg bound' target) args |> finish discard e.Type |> bind bound' argBindings |> bind bound bindings |> Some
            | Op opCall [ Unboxed target ] when FSharpType.IsFunction e.Type ->
                Binders.functionTarget context e.Type (targetArg bound target) |> Some
            | Op opCall [ _ ] ->
                unsupported "Dlr.call read at a non-function type (a value read as a type is Dlr.implicit; to invoke, apply it: Dlr.call x (a, b))" e
            | Op opItem [ indexes; Unboxed target ] ->
                let tupleBindings, indexExprs = splitArgs indexes
                let bound' = withTuple bound tupleBindings
                Binders.getIndex context (targetArg bound target) (indexList bound' indexExprs) |> convert e.Type |> bind bound tupleBindings |> Some
            | Op opSetItem [ indexes; value; Unboxed target ] ->
                let tupleBindings, indexExprs = splitArgs indexes
                let bound' = withTuple bound tupleBindings
                Binders.setIndex context (targetArg bound target) (indexList bound' indexExprs) (valueArg bound' value) |> convert typeof<unit> |> bind bound tupleBindings |> Some
            | BinaryOp(op, Unboxed left, Unboxed right) ->
                Binders.binaryOperation context op (valueArg bound left) (valueArg bound right) |> convert e.Type |> Some
            | UnaryOp(op, Unboxed operand) ->
                Binders.unaryOperation context op (valueArg bound operand) |> convert e.Type |> Some
            | Op opCast [ Unboxed value ] ->
                let v = rewrite value
                Binders.convertExplicit context e.Type (if v.Type = typeof<obj> then v else Expr.Coerce(v, typeof<obj>)) |> Some
            | Op opImplicit [ Unboxed value ] ->
                let v = rewrite value
                convert e.Type (if v.Type = typeof<obj> then v else Expr.Coerce(v, typeof<obj>)) |> Some
            // The marker anywhere but as a target would run its getter at run time and throw the
            // outside-a-block error from inside one; say what is wrong instead. Likewise an
            // argument marker anywhere but in a call's argument list (those were consumed above).
            | StaticTarget _ -> unsupported "Dlr.Static<T>.Overloads anywhere but as the target of a call" e
            | Op opNamed _ -> unsupported "Dlr.named anywhere but as an argument of a call (a member call, Dlr.invoke, Dlr.call / Dlr.apply, Dlr.new')" e
            | Op opNamedOf _ -> unsupported "Dlr.namedOf anywhere but as an argument of a call (a member call, Dlr.invoke, Dlr.call / Dlr.apply, Dlr.new')" e
            | Op opArgsOf _ -> unsupported "Dlr.argsOf anywhere but as an argument of a call (a member call, Dlr.invoke, Dlr.call / Dlr.apply, Dlr.new')" e
            | OutMarker _ | RefMarker _ -> unsupported "Dlr.out / Dlr.outAs / Dlr.ref anywhere but directly in the arguments of a call (x?M(…), Dlr.get, Dlr.invoke, Dlr.call / Dlr.apply; Dlr.ref in Dlr.new')" e
            | TypeArgs _ -> unsupported "Dlr.typeArgs anywhere but as the first argument of a member call" e
            | _ -> None
