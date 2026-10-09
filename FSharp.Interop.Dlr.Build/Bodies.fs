namespace FSharp.Interop.Dlr.Build

open System.Collections.Generic
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Interop.Dlr.BodyMap

/// The capture map's `body` section (#219, stacked on #216): the body of each member that holds a
/// dlr { } block, from the compiler's unoptimized tree, in BodyMap's wire form, so a block needs no
/// [<ReflectedDefinition>]. Each member is written once, at its first block (`X <payload>`); its
/// other blocks refer to that one (`Y file line`). `Companion/oracle.fsx` checks the encoding
/// against the reflected definitions.
module internal Bodies =

    // ---- Encoding: FCS → wire. Names only: nothing here touches reflection.

    let rec typeRef (t: FSharpType) : TypeRef =
        if t.IsAbbreviation then typeRef t.AbbreviatedType
        elif t.IsGenericParameter then TParam t.GenericParameter.Name
        elif t.IsFunctionType then TNamed("FSharp.Core", "Microsoft.FSharp.Core.FSharpFunc`2", [ typeRef t.GenericArguments.[0]; typeRef t.GenericArguments.[1] ])
        elif t.IsStructTupleType then TTuple(true, [ for a in t.GenericArguments -> typeRef a ])
        elif t.IsTupleType then TTuple(false, [ for a in t.GenericArguments -> typeRef a ])
        elif t.IsAnonRecordType then
            let d = t.AnonRecordTypeDetails
            TNamed(d.Assembly.SimpleName, d.CompiledName, [ for a in t.GenericArguments -> typeRef a ])
        else
            let e = t.TypeDefinition
            if e.IsArrayType then TArray(e.ArrayRank, typeRef t.GenericArguments.[0])
            elif e.IsByRef then TByref(typeRef t.GenericArguments.[0])
            elif e.IsFSharpAbbreviation then typeRef (e.AbbreviatedType.Instantiate(Seq.zip e.GenericParameters t.GenericArguments |> List.ofSeq))
            else
                let args = [ for a in t.GenericArguments do if not a.IsMeasureType then typeRef a ]
                TNamed(entityAssembly e, entityName e, args)

    and entityAssembly (e: FSharpEntity) = e.Assembly.SimpleName

    /// The CLI name: namespace, enclosing types joined by `+`, compiled names (a module's `Module` suffix).
    and entityName (e: FSharpEntity) : string =
        match e.DeclaringEntity with
        | Some d when not d.IsNamespace -> entityName d + "+" + e.CompiledName
        | _ ->
            match e.Namespace with
            | Some ns -> ns + "." + e.CompiledName
            | None -> e.CompiledName

    let memberRef (m: FSharpMemberOrFunctionOrValue) : MemberRef =
        let declaring =
            match m.DeclaringEntity with
            | Some e -> TNamed(entityAssembly e, entityName e, [])
            | None -> failwithf "no declaring entity: %s" m.LogicalName
        let parameters =
            [ for g in m.CurriedParameterGroups do
                for p in g do
                    yield typeRef p.Type ]
            // A function of unit compiles with no parameter.
            |> function
                | [ TNamed(_, "Microsoft.FSharp.Core.Unit", []) ] -> []
                | ps -> ps
            // An extension member compiles static, the extended value first.
            |> fun ps ->
                if m.IsExtensionMember && m.IsInstanceMember then
                    let e = m.ApparentEnclosingEntity.Value
                    TNamed(entityAssembly e, entityName e, [ for p in e.GenericParameters -> TParam p.Name ]) :: ps
                else ps
        { Declaring = declaring
          Name = m.CompiledName
          Instance = m.IsConstructor || (m.IsInstanceMember && not m.IsExtensionMember)
          // FCS lists the enclosing type's parameters too.
          GenericArity =
            let enclosing = match m.DeclaringEntity with Some e -> [ for p in e.GenericParameters -> p.Name ] | None -> []
            m.GenericParameters |> Seq.filter (fun p -> not (List.contains p.Name enclosing)) |> Seq.length
          Parameters = parameters
          Return = typeRef m.ReturnParameter.Type }

    // ---- Members that hold a block: a call to the builder's Run with caller information.

    let isRun (m: FSharpMemberOrFunctionOrValue) =
        m.CompiledName = "Run" && (try m.DeclaringEntity.Value.CompiledName = "DlrBuilder" with _ -> false)

    let rec holdsBlock (e: FSharpExpr) =
        match e with
        | FSharpExprPatterns.Call(_, m, _, _, _) when isRun m -> true
        | _ -> e.ImmediateSubExpressions |> List.exists holdsBlock

    /// Every member whose body holds a block, with its parameters, from the unoptimized tree.
    let members (contents: FSharpAssemblyContents) =
        let found = ResizeArray()
        let rec go (ds: FSharpImplementationFileDeclaration list) =
            for d in ds do
                match d with
                | FSharpImplementationFileDeclaration.Entity(_, sub) -> go sub
                | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(m, args, body) when holdsBlock body -> found.Add((m, args, body))
                | _ -> ()
        for f in contents.ImplementationFiles do go f.Declarations
        List.ofSeq found

    // ---- The tree: FSharpExpr → wire.

    /// FCS node kinds the wire form has no case for, with counts (a member holding one is not written).
    let unsupported = Dictionary<string, int>()
    let mutable unsupportedTotal = 0
    let note (s: string) =
        unsupportedTotal <- unsupportedTotal + 1
        unsupported.[s] <- (match unsupported.TryGetValue s with | true, n -> n + 1 | _ -> 1)

    let encode (args: FSharpMemberOrFunctionOrValue list list) (body: FSharpExpr) : W =
        let ids = Dictionary<FSharpMemberOrFunctionOrValue, int>()
        let mutable next = 0
        let def (v: FSharpMemberOrFunctionOrValue) =
            next <- next + 1
            ids.[v] <- next
            { Id = next; Name = v.LogicalName; Type = typeRef v.FullType; Mutable = v.IsMutable }
        let fresh (name: string) (t: TypeRef) =
            next <- next + 1
            { Id = next; Name = name; Type = t; Mutable = false }
        let targetsStack = Stack<(FSharpMemberOrFunctionOrValue list * FSharpExpr) list>()
        /// A curried parameter of tuple type, which FCS shows split (`u_0`, `u_1`): element → (tuple, index).
        let split = Dictionary<FSharpMemberOrFunctionOrValue, VarDef * int>()
        let isSplitOf (name: string) (x: FSharpExpr) =
            match x with
            | FSharpExprPatterns.Value w -> (match split.TryGetValue w with | true, (d, _) -> d.Name = name | _ -> false)
            | _ -> false
        /// A function of unit compiles with no parameter: a `()` argument goes, and an argument
        /// with an effect runs first, as the quotation sequences it.
        let rec withoutUnit (m: FSharpMemberOrFunctionOrValue) (r: MemberRef) (xs: FSharpExpr list) (call: FSharpExpr list -> W) : W =
            let declared = if m.IsExtensionMember && m.IsInstanceMember then List.tail r.Parameters else r.Parameters
            if not declared.IsEmpty then call xs
            else
                let units, rest = xs |> List.partition (fun x -> typeRef x.Type = TNamed("FSharp.Core", "Microsoft.FSharp.Core.Unit", []))
                let effects = units |> List.filter (function FSharpExprPatterns.Const _ -> false | _ -> true)
                List.foldBack (fun x w -> WSeq(go x, w)) effects (call rest)
        and go (e: FSharpExpr) : W =
            match e with
            | FSharpExprPatterns.Value v when split.ContainsKey v -> let d, i = split.[v] in WTupleGet(d.Type, i, WVar d.Id)
            // The body rebuilding the split tuple is the parameter itself.
            | FSharpExprPatterns.Let((v, FSharpExprPatterns.NewTuple(_, (_ :: _ as xs)), _), b) when List.forall (isSplitOf v.LogicalName) xs ->
                let d, _ = (match xs.Head with FSharpExprPatterns.Value w -> split.[w] | _ -> failwith "unreachable")
                ids.[v] <- d.Id
                go b
            | FSharpExprPatterns.Value v when ids.ContainsKey v -> WVar ids.[v]
            | FSharpExprPatterns.Value v when v.IsModuleValueOrMember ->
                WStaticValue(TNamed(entityAssembly v.DeclaringEntity.Value, entityName v.DeclaringEntity.Value, []), v.CompiledName)
            | FSharpExprPatterns.ValueSet(v, x) when ids.ContainsKey v -> WVarSet(ids.[v], go x)
            // A module's mutable: its static property.
            | FSharpExprPatterns.ValueSet(v, x) when v.IsModuleValueOrMember && v.DeclaringEntity.IsSome ->
                WStaticSet(TNamed(entityAssembly v.DeclaringEntity.Value, entityName v.DeclaringEntity.Value, []), v.CompiledName, go x)
            | FSharpExprPatterns.Lambda(v, b) -> let d = def v in WLambda(d, go b)
            | FSharpExprPatterns.Let((v, x, _), b) ->
                let x = go x
                let d = def v
                WLet(d, x, go b)
            | FSharpExprPatterns.LetRec(bs, b) ->
                let ds = [ for (v, _, _) in bs -> def v ]
                WLetRec([ for d, (_, x, _) in List.zip ds bs -> d, go x ], go b)
            | FSharpExprPatterns.Application(f, _, xs) -> List.fold (fun f x -> WApp(f, go x)) (go f) xs
            // A constant the text carries (a primitive, a string, a char, a decimal, null); any other
            // (a byte array, a nativeint) leaves the member out.
            | FSharpExprPatterns.Const(v, t) when isNull v || (match System.Type.GetTypeCode(v.GetType()) with System.TypeCode.Object | System.TypeCode.Empty | System.TypeCode.DBNull -> false | _ -> true) ->
                WConst(v, typeRef t)
            | FSharpExprPatterns.Const(v, _) ->
                let kind = "constant " + v.GetType().Name
                note kind
                WUnsupported kind
            | FSharpExprPatterns.DefaultValue t -> WDefault(typeRef t)
            | FSharpExprPatterns.NewObject(m, targs, xs) ->
                let r = memberRef m
                withoutUnit m r xs (fun xs -> WNewObject(r, List.map typeRef targs, List.map go xs))
            // A module value: its static property.
            | FSharpExprPatterns.Call(None, m, _, _, []) when not m.IsMember && m.CurriedParameterGroups.Count = 0 && m.GenericParameters.Count = 0 && m.DeclaringEntity.IsSome && m.DeclaringEntity.Value.IsFSharpModule ->
                WStaticValue(TNamed(entityAssembly m.DeclaringEntity.Value, entityName m.DeclaringEntity.Value, []), m.CompiledName)
            | FSharpExprPatterns.ILFieldGet(o, t, name) -> WFieldGet(Option.map go o, typeRef t, name)
            | FSharpExprPatterns.ILFieldSet(o, t, name, x) -> WFieldSet(Option.map go o, typeRef t, name, go x)
            | FSharpExprPatterns.AnonRecordGet(x, t, i) -> WFieldGet(Some(go x), typeRef t, t.AnonRecordTypeDetails.SortedFieldNames.[i])
            | FSharpExprPatterns.AddressOf x -> go x
            | FSharpExprPatterns.CallWithWitnesses(o, m, targs, margs, ws, xs) when not ws.IsEmpty ->
                let r = memberRef m
                let o, xs = if m.IsExtensionMember then None, Option.toList o @ xs else o, xs
                WCallW(Option.map go o, r, List.map typeRef targs, List.map typeRef margs, List.map go ws, List.map go xs)
            | FSharpExprPatterns.Call(o, m, targs, margs, xs) ->
                let r = memberRef m
                withoutUnit m r xs (fun xs ->
                    // An extension member's target is its first argument.
                    let o, xs = if m.IsExtensionMember then None, Option.toList o @ xs else o, xs
                    WCall(Option.map go o, r, List.map typeRef targs, List.map typeRef margs, List.map go xs))
            | FSharpExprPatterns.NewRecord(t, xs) -> WNewRecord(typeRef t, List.map go xs)
            | FSharpExprPatterns.NewAnonRecord(t, xs) -> WNewRecord(typeRef t, List.map go xs)
            | FSharpExprPatterns.NewUnionCase(t, c, xs) -> WNewUnion(typeRef t, c.CompiledName, List.map go xs)
            | FSharpExprPatterns.UnionCaseTest(x, t, c) -> WUnionTest(go x, typeRef t, c.CompiledName)
            | FSharpExprPatterns.UnionCaseGet(x, t, c, f) ->
                WUnionGet(go x, typeRef t, c.CompiledName, c.Fields |> Seq.findIndex (fun g -> g.Name = f.Name))
            | FSharpExprPatterns.NewTuple(t, xs) -> WNewTuple(typeRef t, List.map go xs)
            | FSharpExprPatterns.TupleGet(t, i, x) -> WTupleGet(typeRef t, i, go x)
            | FSharpExprPatterns.FSharpFieldGet(o, t, f) -> WFieldGet(Option.map go o, typeRef t, f.Name)
            | FSharpExprPatterns.FSharpFieldSet(o, t, f, x) -> WFieldSet(Option.map go o, typeRef t, f.Name, go x)
            | FSharpExprPatterns.IfThenElse(c, a, b) -> WIf(go c, go a, go b)
            | FSharpExprPatterns.Sequential(a, b) -> WSeq(go a, go b)
            | FSharpExprPatterns.WhileLoop(c, b, _) -> WWhile(go c, go b)
            | FSharpExprPatterns.FastIntegerForLoop(lo, hi, FSharpExprPatterns.Lambda(v, b), true, _, _) ->
                let lo, hi = go lo, go hi
                let d = def v
                WFor(d, lo, hi, go b)
            | FSharpExprPatterns.TryWith(b, fv, f, cv, c, _, _) ->
                let b = go b
                let fd = def fv
                let f = go f
                let cd = def cv
                WTryWith(b, fd, f, cd, go c)
            | FSharpExprPatterns.TryFinally(b, f, _, _) -> WTryFinally(go b, go f)
            | FSharpExprPatterns.Coerce(t, x) -> WCoerce(typeRef t, go x)
            | FSharpExprPatterns.TypeTest(t, x) -> WTypeTest(typeRef t, go x)
            | FSharpExprPatterns.NewArray(t, xs) -> WNewArray(typeRef t, List.map go xs)
            | FSharpExprPatterns.NewDelegate(t, x) ->
                // As many lambdas as Invoke takes parameters; a delegate of none has the unit lambda's.
                let rec strip (t: FSharpType) = if t.IsAbbreviation then strip t.AbbreviatedType else t
                let arity =
                    (strip t).TypeDefinition.MembersFunctionsAndValues |> Seq.find (fun m -> m.CompiledName = "Invoke")
                    |> fun m -> m.CurriedParameterGroups |> Seq.sumBy (fun g -> g.Count)
                let rec lambdas n acc x =
                    match x with
                    | FSharpExprPatterns.Lambda(v, b) when n > 0 -> lambdas (n - 1) (v :: acc) b
                    | b -> List.rev acc, b
                let vs, b = lambdas (max 1 arity) [] x
                let ds = List.map def vs
                WNewDelegate(typeRef t, ds, go b)
            // `<@@ … @@>` is a raw quotation (type Expr), `<@ … @>` a typed one.
            | FSharpExprPatterns.Quote x -> WQuote(typeRef e.Type = TNamed("FSharp.Core", "Microsoft.FSharp.Quotations.FSharpExpr", []), go x)
            // A trait call in a witness: the member the constraint resolves to, a plain call.
            | FSharpExprPatterns.TraitCall(sources, name, flags, _, argTypes, xs) ->
                let xs = List.map go xs
                let o, xs = if flags.IsInstance then Some xs.Head, xs.Tail else None, xs
                WTraitCall(typeRef sources.Head, name, o, [ for t in argTypes -> typeRef t ], xs)
            | FSharpExprPatterns.DecisionTree(d, targets) ->
                targetsStack.Push targets
                let r = go d
                targetsStack.Pop() |> ignore
                r
            | FSharpExprPatterns.DecisionTreeSuccess(i, xs) ->
                let targets = targetsStack.Pop()
                let vs, b = targets.[i]
                let xs = List.map go xs
                targetsStack.Push targets
                // Each use of a target is its own copy, as the quotation has it.
                let ds = List.map def vs
                let b = go b
                List.foldBack2 (fun d x b -> WLet(d, x, b)) ds xs b
            | FSharpExprPatterns.DebugPoint(_, x) -> go x
            | _ ->
                let kind = (sprintf "%A" e).Split([| '(' ; ' '; '\n' |]).[0]
                note kind
                WUnsupported kind
        // The member's parameters, as the quotation of a member binds them.
        let rec wrap (groups: FSharpMemberOrFunctionOrValue list list) =
            match groups with
            | [] -> go body
            | [ v ] :: rest -> let d = def v in WLambda(d, wrap rest)
            | [] :: rest -> WLambda(fresh "unitVar" (TNamed("FSharp.Core", "Microsoft.FSharp.Core.Unit", [])), wrap rest)
            | group :: rest when
                group.Length > 1
                && group |> List.mapi (fun i (v: FSharpMemberOrFunctionOrValue) -> v.LogicalName.EndsWith("_" + string i)) |> List.forall id
                && (group |> List.map (fun v -> v.LogicalName.Substring(0, v.LogicalName.LastIndexOf '_')) |> List.distinct |> List.length) = 1 ->
                let name = group.Head.LogicalName.Substring(0, group.Head.LogicalName.LastIndexOf '_')
                let t = TTuple(false, [ for v in group -> typeRef v.FullType ])
                let d = fresh name t
                group |> List.iteri (fun i v -> split.[v] <- (d, i))
                WLambda(d, wrap rest)
            | group :: rest ->
                let t = TTuple(false, [ for v in group -> typeRef v.FullType ])
                let tupled = fresh "tupledArg" t
                let ds = List.map def group
                WLambda(tupled, List.foldBack (fun (i, d) b -> WLet(d, WTupleGet(t, i, WVar tupled.Id), b)) (List.indexed ds) (wrap rest))
        wrap args

    /// The block keys in a member: each Run's caller information (file, line).
    let rec runs (e: FSharpExpr) : (string * int) list =
        [ match e with
          | FSharpExprPatterns.Call(_, r, _, _, xs) when isRun r ->
              match List.rev xs with
              | FSharpExprPatterns.Const(:? int as line, _) :: FSharpExprPatterns.Const(:? string as file, _) :: _ -> yield file, line
              | _ -> ()
          | _ -> ()
          for x in e.ImmediateSubExpressions do yield! runs x ]

    /// The type declaring a member: the binder's accessibility context.
    let context (m: FSharpMemberOrFunctionOrValue) =
        TNamed(entityAssembly m.DeclaringEntity.Value, entityName m.DeclaringEntity.Value, [])

    /// Per block key (file, line), the lines of its `body` section (without the `S body` header):
    /// `X <payload>` at a member's first block, `Y file line` at its others, and `A n` for a line
    /// where n members have a block (the run time refuses it, as it does from reflected definitions).
    let run (results: FSharpCheckProjectResults) : IDictionary<string * int, string list> =
        let sections = Dictionary<string * int, string list>()
        let encoded =
            [ for (m, args, body) in members results.AssemblyContents ->
                let keys = runs body |> List.distinct
                let before = unsupportedTotal
                // A member with a node the wire form has no case for, or one the encoder fails on, is
                // left out: its blocks keep needing the attribute. The build never fails for it.
                let w =
                    try
                        let w = encode args body
                        if unsupportedTotal = before then Some w else None
                    with e ->
                        note ("encoder: " + e.GetType().Name)
                        None
                m, keys, w ]
        let owners = Dictionary<string * int, int>()
        for (_, keys, _) in encoded do
            for k in keys do owners.[k] <- (match owners.TryGetValue k with | true, n -> n + 1 | _ -> 1)
        for KeyValue(k, n) in owners do
            if n > 1 then sections.[k] <- [ sprintf "A\t%d" n ]
        for (m, keys, w) in encoded do
            match w, keys |> List.filter (fun k -> owners.[k] = 1) with
            | Some w, ((file, line) as first :: rest) ->
                sections.[first] <- [ "X\t" + encodeEntry (context m) w ]
                for k in rest do sections.[k] <- [ sprintf "Y\t%s\t%d" file line ]
            | _ -> ()
        sections :> _
