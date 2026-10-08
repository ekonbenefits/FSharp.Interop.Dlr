namespace FSharp.Interop.Dlr

open System
open System.Reflection
open System.Reflection.Emit
open FSharp.Quotations
open FSharp.Quotations.Patterns
open FSharp.Quotations.ExprShape

/// Which of a state machine's same-named fields (`x`, `x0`, `x1`: the compiler numbers them in an
/// order of its own) each variable of that name is, read off `MoveNext`'s IL (#200 spike). The
/// compiler emits a block's code in evaluation order, left to right, depth first, and every marker
/// is a real NoInlining call, so between two marker calls the IL's loads of those fields and the
/// quotation's reads of those variables pair up in order. Any disagreement (count, type, marker
/// sequence, a read inside a nested lambda) gives None, and the block is refused as before.
module internal IlOrder =

    type private Event =
        | Read of obj
        | Landmark of string

    let private opCodes =
        let one = Array.zeroCreate<OpCode option> 256
        let two = Array.zeroCreate<OpCode option> 256
        for f in typeof<OpCodes>.GetFields(BindingFlags.Public ||| BindingFlags.Static) do
            let op = f.GetValue null :?> OpCode
            let v = uint16 op.Value
            if v < 0x100us then one.[int v] <- Some op
            elif v &&& 0xFF00us = 0xFE00us then two.[int (v &&& 0xFFus)] <- Some op
        one, two

    let private isMarker (m: MethodBase) =
        not (isNull m.DeclaringType)
        && m.DeclaringType.Assembly = Assembly.GetExecutingAssembly()
        && (let n = m.DeclaringType.FullName
            n.StartsWith "FSharp.Interop.Dlr.Operators" || n.StartsWith "FSharp.Interop.Dlr.Dlr")

    /// A method's instructions: each opcode with its operand when it is a token or a local's or
    /// argument's index.
    let private instructions (m: MethodBase) : (OpCode * int) list option =
        match m.GetMethodBody() with
        | null -> None
        | body ->
        let il = body.GetILAsByteArray()
        let one, two = opCodes
        let result = ResizeArray()
        let mutable i = 0
        let mutable ok = true
        while ok && i < il.Length do
            let op = if il.[i] = 0xFEuy then (i <- i + 1; two.[int il.[i]]) else one.[int il.[i]]
            i <- i + 1
            match op with
            | None -> ok <- false
            | Some op ->
                let size =
                    match op.OperandType with
                    | OperandType.InlineNone -> 0
                    | OperandType.ShortInlineBrTarget | OperandType.ShortInlineI | OperandType.ShortInlineVar -> 1
                    | OperandType.InlineVar -> 2
                    | OperandType.InlineI8 | OperandType.InlineR -> 8
                    | OperandType.InlineSwitch -> 4 + 4 * BitConverter.ToInt32(il, i)
                    | _ -> 4
                let operand =
                    match size with
                    | 4 -> BitConverter.ToInt32(il, i)
                    | 2 -> int (BitConverter.ToUInt16(il, i))
                    | 1 -> int il.[i]
                    | _ -> 0
                result.Add((op, operand))
                i <- i + size
        if ok then Some(List.ofSeq result) else None

    /// The IL's reads of the machine's fields in `family`, in order, as the machine field each is.
    /// A closure the code creates (a loop or try body, a local function kept as a closure) takes
    /// some of them as constructor arguments: those loads are captures, not reads, so the
    /// closure's own reads stand in their place, its fields mapped back to the machine's through
    /// its constructor, by position, and by name as a check.
    let private ilEvents (machine: Type) (family: string -> bool) : Event list option =
        let typeArgs = if machine.IsGenericType then machine.GetGenericArguments() else null
        let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly
        let isClosure (t: Type) =
            not (isNull t) && t.Assembly = machine.Assembly && not t.IsValueType && t.Name.Contains "@" && t.Name <> machine.Name
        /// The fields a closure's constructor stores, in order.
        let stores (ctor: ConstructorInfo) =
            instructions ctor
            |> Option.map (List.choose (fun (op, token) ->
                if op = OpCodes.Stfld then Some((ctor.Module.ResolveField(token, typeArgs, null)).Name) else None))
        let rec reads (depth: int) (m: MethodBase) (owner: Type) (machineName: string -> string option) : string list option =
            if depth > 8 then None
            else
            match instructions m with
            | None -> None
            | Some code ->
            let result = ResizeArray<string>()
            let mutable ok = true
            for (op, token) in code do
                if ok then
                    if (op = OpCodes.Ldfld || op = OpCodes.Ldflda) then
                        let f = m.Module.ResolveField(token, typeArgs, null)
                        if f.DeclaringType.Name = owner.Name then
                            match machineName f.Name with
                            | Some n -> result.Add n
                            | None -> ()
                    elif op = OpCodes.Newobj then
                        let ctor = m.Module.ResolveMethod(token, typeArgs, null) :?> ConstructorInfo
                        if isClosure ctor.DeclaringType then
                            match stores ctor with
                            | None -> ok <- false
                            | Some stored ->
                                let captured = stored |> List.filter family
                                let k = captured.Length
                                if k > 0 then
                                    // The last k reads are its captures, stored in order, under the same names.
                                    let args = if result.Count >= k then List.ofSeq (result.GetRange(result.Count - k, k)) else []
                                    if args.Length <> k || args <> captured then ok <- false
                                    else
                                        result.RemoveRange(result.Count - k, k)
                                        let map = List.zip captured args |> dict
                                        let closureName (f: string) = match map.TryGetValue f with | true, n -> Some n | _ -> None
                                        match ctor.DeclaringType.GetMethods(flags) |> Array.filter (fun mi -> mi.Name = "Invoke") with
                                        | [| invoke |] ->
                                            match reads (depth + 1) invoke ctor.DeclaringType closureName with
                                            | Some inner -> result.AddRange inner
                                            | None -> ok <- false
                                        | _ -> ok <- false
            if ok then Some(List.ofSeq result) else None
        match machine.GetMethod("MoveNext", BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) with
        | null -> None
        | moveNext -> reads 0 moveNext machine (fun f -> if family f then Some f else None) |> Option.map (List.map (box >> Read))

    /// When the value of each machine field in `family` was computed, in the method that fills the
    /// machine: the member evaluates its bindings in source order (their effects must stay in
    /// order), keeps each in a local, and copies it into the machine (`ldloc k; stfld x_00`). The
    /// position of the last store to that local before the copy orders the fields by binding, a
    /// signal of its own: data flow, not names nor declaration order. A parameter comes first.
    /// None for a field whose value is not a local's or a parameter's.
    let private storeOrder (machine: Type) (family: string -> bool) : Collections.Generic.IDictionary<string, int option> option =
        let flags = BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly
        let rec types (t: Type) = seq { yield t; for n in t.GetNestedTypes(flags) do yield! types n }
        let generic (m: MethodBase) =
            (if m.DeclaringType.IsGenericType then m.DeclaringType.GetGenericArguments() else null),
            (if m.IsGenericMethod then m.GetGenericArguments() else null)
        let fieldOf (m: MethodBase) (token: int) =
            let t, g = generic m
            try Some(m.Module.ResolveField(token, t, g)) with _ -> None
        let onMachine (f: FieldInfo option) = match f with Some f -> f.DeclaringType.Name = machine.Name | None -> false
        if isNull machine.DeclaringType then None
        else
        let fills =
            types machine.DeclaringType
            |> Seq.filter (fun t -> t.Name <> machine.Name)
            |> Seq.collect (fun t -> Seq.append (t.GetMethods flags |> Seq.cast<MethodBase>) (t.GetConstructors flags |> Seq.cast<MethodBase>))
            |> Seq.tryPick (fun m ->
                match instructions m with
                | Some code when code |> List.exists (fun (op, token) -> op = OpCodes.Stfld && onMachine (fieldOf m token)) -> Some(m, code)
                | _ -> None)
        match fills with
        | None -> None
        | Some(m, code) ->
        let local (op: OpCode) operand =
            if op = OpCodes.Ldloc_0 || op = OpCodes.Stloc_0 then Some 0
            elif op = OpCodes.Ldloc_1 || op = OpCodes.Stloc_1 then Some 1
            elif op = OpCodes.Ldloc_2 || op = OpCodes.Stloc_2 then Some 2
            elif op = OpCodes.Ldloc_3 || op = OpCodes.Stloc_3 then Some 3
            elif op = OpCodes.Ldloc_S || op = OpCodes.Ldloc || op = OpCodes.Stloc_S || op = OpCodes.Stloc then Some operand
            else None
        let isStore (op: OpCode) = op = OpCodes.Stloc_0 || op = OpCodes.Stloc_1 || op = OpCodes.Stloc_2 || op = OpCodes.Stloc_3 || op = OpCodes.Stloc_S || op = OpCodes.Stloc
        let isLoad (op: OpCode) = op = OpCodes.Ldloc_0 || op = OpCodes.Ldloc_1 || op = OpCodes.Ldloc_2 || op = OpCodes.Ldloc_3 || op = OpCodes.Ldloc_S || op = OpCodes.Ldloc
        let argument (op: OpCode) operand =
            if op = OpCodes.Ldarg_0 then Some 0 elif op = OpCodes.Ldarg_1 then Some 1
            elif op = OpCodes.Ldarg_2 then Some 2 elif op = OpCodes.Ldarg_3 then Some 3
            elif op = OpCodes.Ldarg_S || op = OpCodes.Ldarg then Some operand
            else None
        let lastStore = Collections.Generic.Dictionary<int, int>()
        let result = Collections.Generic.Dictionary<string, int option>()
        let code = Array.ofList code
        for i in 0 .. code.Length - 1 do
            let op, operand = code.[i]
            if isStore op then lastStore.[(local op operand).Value] <- i
            elif op = OpCodes.Stfld && i > 0 then
                match fieldOf m operand with
                | Some f when f.DeclaringType.Name = machine.Name && family f.Name ->
                    let pop, poperand = code.[i - 1]
                    let at =
                        if isLoad pop then
                            match lastStore.TryGetValue((local pop poperand).Value) with
                            | true, at -> Some at
                            | _ -> None
                        else argument pop poperand |> Option.map (fun a -> a - 100000)
                    result.[f.Name] <- at
                | _ -> ()
        Some(result :> _)

    /// The quotation's events, in evaluation order: reads of variables named in `family`, and
    /// marker calls. A variable with no field is the optimizer's to inline: its recovered
    /// definition (`recover`) stands where it is read, a local function's body after its
    /// arguments. A builder call's lambdas are its loop and try bodies, inline in `MoveNext`.
    let private quotationEvents (isBuilder: Expr option -> bool) (hasField: Var -> bool) (recover: Var -> Expr option) (substituted: Var -> bool) (assumed: Collections.Generic.HashSet<Var>) (wholePlain: bool) (family: string -> bool) (body: Expr) : Event list option =
        let events = ResizeArray()
        let mutable ok = true
        let opaque = Collections.Generic.HashSet<Var>()
        // Each give-up names its reason where it is called.
        let fail (_reason: string) (_at: Expr) =
            ok <- false
        let rec lambdaBody (e: Expr) =
            match e with
            | Lambda(_, b) -> lambdaBody b
            | _ -> e
        /// The tuple variable of a shared name `e` is, seen through what the optimizer inlines: an
        /// alias, or a local function applied to unit whose body is it.
        /// A tuple of constants and immutable variables of other names: the optimizer substitutes
        /// its elements, which then have no field and no read (if the IL agrees).
        let plain (v: Var) =
            not v.IsMutable
            && (match recover v with
                | Some(NewTuple es) -> es |> List.forall (function Value _ -> true | Var y -> not y.IsMutable && not (family y.Name) | _ -> false)
                | _ -> false)
        let rec tupleOf (e: Expr) : Var option =
            match e with
            | Var v when family v.Name -> Some v
            | Var a when not (family a.Name) && not a.IsMutable ->
                // An alias of an immutable variable: substituted, or (if the IL agrees) assumed so.
                match recover a with
                | Some(Var y as d) when not y.IsMutable ->
                    let found = tupleOf d
                    if found.IsSome && not (substituted a) then assumed.Add a |> ignore
                    found
                | _ -> None
            | Application(Var f, Value(_, t)) when t = typeof<unit> && not (hasField f) ->
                match recover f with
                | Some(Lambda(_, b)) -> tupleOf b
                | _ -> None
            | _ -> None
        let rec walk (depth: int) (e: Expr) =
            if not ok then () elif depth > 64 then fail "depth" e
            else
            match e with
            | Var v when opaque.Contains v -> ()
            | TupleGet(inner, _) when (tupleOf inner).IsSome && plain (tupleOf inner).Value -> assumed.Add (tupleOf inner).Value |> ignore
            | Call(None, mi, [ inner ]) when mi.DeclaringType.FullName = "Microsoft.FSharp.Core.Operators" && (mi.Name = "Fst" || mi.Name = "Snd") && (tupleOf inner).IsSome && plain (tupleOf inner).Value ->
                assumed.Add (tupleOf inner).Value |> ignore
            | TupleGet(inner, i) when (tupleOf inner).IsSome -> events.Add(Read(box ((tupleOf inner).Value, Some i)))
            | Call(None, mi, [ inner ]) when mi.DeclaringType.FullName = "Microsoft.FSharp.Core.Operators" && (mi.Name = "Fst" || mi.Name = "Snd") && (tupleOf inner).IsSome ->
                events.Add(Read(box ((tupleOf inner).Value, Some(if mi.Name = "Fst" then 0 else 1))))
            | Var v when family v.Name && substituted v -> ()
            // A tuple of constants (and other names' immutable variables) read whole: substituted
            // (if the IL agrees).
            | Var v when family v.Name && wholePlain && plain v -> assumed.Add v |> ignore
            | Var v when family v.Name && not v.IsMutable && (match recover v with Some(Var y) -> not y.IsMutable && not (family y.Name) | _ -> false) ->
                // An alias the optimizer substitutes has no field and no read; if the IL agrees
                // (no read left over), it is read through its definition.
                assumed.Add v |> ignore
            | Var v when family v.Name -> events.Add(Read(box (v, (None: int option))))
            | Var v when not (hasField v) ->
                match recover v with
                | Some(Lambda _) | None -> ()
                | Some(LetRecursive _) -> fail "let rec" e
                | Some def -> walk (depth + 1) def
            | VarSet(v, value) ->
                if family v.Name then events.Add(Read(box (v, (None: int option))))
                walk depth value
            | Application _ ->
                // `f a b`: the function, unless it is one the optimizer inlined, then its arguments,
                // then the inlined body.
                let rec spine (e: Expr) acc =
                    match e with
                    | Application(f, a) -> spine f (a :: acc)
                    | f -> f, acc
                let f, args = spine e []
                match f with
                | Var fv when opaque.Contains fv -> for a in args do walk depth a
                | Var fv when not (family fv.Name) && not (hasField fv) ->
                    match recover fv with
                    | Some(Lambda _ as def) ->
                        for a in args do walk depth a
                        // Its parameters are bound here, at the call (a local of MoveNext, or of the
                        // closure's Invoke): never fields.
                        let rec parameters (e: Expr) = match e with Lambda(p, b) -> opaque.Add p |> ignore; parameters b | _ -> ()
                        parameters def
                        walk (depth + 1) (lambdaBody def)
                    | Some(LetRecursive(bindings, _)) ->
                        // Not inlined: a closure created here, whose body runs (once, as far as
                        // the order of reads goes) where it is called; its calls to its group are
                        // its own.
                        for a in args do walk depth a
                        for (v, _) in bindings do opaque.Add v |> ignore
                        match bindings |> List.tryFind (fun (v, _) -> v = fv) with
                        | Some(_, def) -> walk (depth + 1) (lambdaBody def)
                        | None -> fail "rec group" e
                    | _ -> fail "function not recovered" e
                | Lambda _ ->
                    for a in args do walk depth a
                    walk depth (lambdaBody f)
                | _ ->
                    walk depth f
                    for a in args do walk depth a
            | Call(target, _, args) when isBuilder target ->
                target |> Option.iter (walk depth)
                for a in args do
                    match a with
                    | Lambda _ ->
                        let rec parameters (e: Expr) = match e with Lambda(p, b) -> opaque.Add p |> ignore; parameters b | _ -> ()
                        parameters a
                        walk depth (lambdaBody a)
                    | _ -> walk depth a
            | Call(target, mi, args) ->
                target |> Option.iter (walk depth)
                for a in args do walk depth a
                if isMarker mi then events.Add(Landmark mi.Name)
            // Binding forms: the generic shape gives their bodies as lambdas, which they are not.
            // Variables bound inside the block are its own, never captures: not recovered.
            | Let(v, d, b) -> walk depth d; opaque.Add v |> ignore; walk depth b
            | ForIntegerRangeLoop(v, a, b, body) -> walk depth a; walk depth b; opaque.Add v |> ignore; walk depth body
            | TryWith(body, fv, filter, cv, handler) ->
                walk depth body; opaque.Add fv |> ignore; opaque.Add cv |> ignore; walk depth filter; walk depth handler
            | Lambda _ ->
                // Inlined where it stands, or a closure created there whose reads `ilEvents`
                // follows to the same place: either way its reads come here.
                let rec parameters (e: Expr) = match e with Lambda(p, b) -> opaque.Add p |> ignore; parameters b | _ -> ()
                parameters e
                walk depth (lambdaBody e)
            | ShapeVar _ -> ()
            | ShapeLambda _ -> ()
            | ShapeCombination(_, es) -> for x in es do walk depth x
        walk 0 body
        if ok then Some(List.ofSeq events) else None

    /// Each variable named in `names`, and the field of the machine it is, when the IL and the
    /// quotation agree; None otherwise.
    let resolve (machine: Type) (fields: Collections.Generic.IDictionary<string, FieldInfo>) (isBuilder: Expr option -> bool) (recover: Var -> Expr option) (substituted: Var -> bool) (memberBody: Expr) (names: Set<string>) (body: Expr) : (Map<Var * int option, FieldInfo> * Set<Var>) option =
        // Where each variable is bound in the member, in source order (a lambda's parameter, a
        // let's variable, a pattern's).
        let bindingOrder =
            let order = Collections.Generic.Dictionary<Var, int>(HashIdentity.Reference)
            let rec go (e: Expr) =
                match e with
                | ShapeVar _ -> ()
                | ShapeLambda(v, b) -> (if not (order.ContainsKey v) then order.[v] <- order.Count); go b
                | Let(v, d, b) -> go d; (if not (order.ContainsKey v) then order.[v] <- order.Count); go b
                | ShapeCombination(_, es) -> for x in es do go x
            go memberBody
            fun (v: Var) -> match order.TryGetValue v with | true, i -> i | _ -> Int32.MaxValue
        if not machine.IsValueType || names.IsEmpty then None
        else
        // `x`, `x0`, `x1`, … for each name, and a split tuple's `x_0`, `x_0$tupleElem`, `x0_1`, ….
        // The compiler appends its uniquifying digits to the whole name: `x_0` then `x_00`,
        // `x_0$tupleElem` then `x_0$tupleElem0`. An element's digits are its index then those.
        let parse (field: string) : (string * string option) option =
            names
            |> Seq.tryPick (fun n ->
                if not (field.StartsWith n) then None
                else
                let rest = field.Substring(n.Length).Replace("$tupleElem", "")
                let digits (s: string) = s |> Seq.forall Char.IsDigit
                match rest.IndexOf '_' with
                | -1 when digits rest -> Some(n, None)
                | -1 -> None   // another name it begins (`xs` for `x`)
                | at when digits (rest.Substring(0, at)) && rest.Length > at + 1 && digits (rest.Substring(at + 1)) -> Some(n, Some(rest.Substring(at + 1)))
                | _ -> None)
        let hasField (v: Var) = fields.ContainsKey v.Name
        // Landmarks are dropped: an over-applied marker (`o?M(a, b)`) is a function value called
        // through FSharpFunc.InvokeFast in the IL, not a call to the marker.
        let reads events = events |> Option.map (List.choose (function Read r -> Some r | Landmark _ -> None))
        // First as read; then, if that does not pair, with a tuple of constants read whole taken as
        // substituted too (a whole read keeps its field as often as not).
        let attempt (wholePlain: bool) =
            let assumed = Collections.Generic.HashSet<Var>()
            let ilR, qR = reads (ilEvents machine (fun f -> (parse f).IsSome)), reads (quotationEvents isBuilder hasField recover substituted assumed wholePlain names.Contains body)
            // A field the family takes in (`x1`, `x_0`) that is also the name of one of the member's
            // own variables may be that variable's: nothing tells which, so refuse.
            let memberNames =
                let names = Collections.Generic.HashSet<string>()
                let rec go (e: Expr) =
                    match e with
                    | ShapeVar v -> names.Add v.Name |> ignore
                    | ShapeLambda(v, b) -> names.Add v.Name |> ignore; go b
                    | ShapeCombination(_, es) -> for x in es do go x
                go memberBody
                names
            let clash =
                fields.Keys |> Seq.exists (fun f -> not (names.Contains f) && (parse f).IsSome && memberNames.Contains f)
            match ilR, qR with
            | _ when clash -> None
            | Some il, Some q when il.Length = q.Length ->
                let fits (f: FieldInfo) (v: Var) (element: int option) =
                    match element, parse f.Name with
                    | None, Some(_, None) ->
                        f.FieldType = v.Type
                        || (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() = typedefof<Ref<_>> && f.FieldType.GetGenericArguments().[0] = v.Type)
                    | Some i, Some(_, Some digits) ->
                        digits.StartsWith(string i) && Reflection.FSharpType.IsTuple v.Type
                        && f.FieldType = (Reflection.FSharpType.GetTupleElements v.Type).[i]
                    // An element of a tuple kept whole (a mutable one is never split).
                    | Some _, Some(_, None) ->
                        f.FieldType = v.Type
                        || (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() = typedefof<Ref<_>> && f.FieldType.GetGenericArguments().[0] = v.Type)
                    | _ -> false
                let mutable byKey = Map.empty<Var * int option, FieldInfo>
                // A field is one variable's (its whole and an element may share a tuple kept whole).
                let mutable byField = Map.empty<string, Var>
                let mutable ok = true
                for (fieldName, read) in List.zip il q do
                    match fields.TryGetValue(unbox<string> fieldName) with
                    | false, _ -> ok <- false
                    | true, f ->
                    let v, element = unbox<Var * int option> read
                    // A whole read the optimizer narrowed to one element (the rest unread): that
                    // element's, by the index its field's name begins with.
                    let element =
                        match element, parse f.Name with
                        | None, Some(_, Some digits) when Reflection.FSharpType.IsTuple v.Type -> Some(int (string digits.[0]))
                        | _ -> element
                    if not (fits f v element) then ok <- false
                    match byKey.TryFind(v, element), byField.TryFind f.Name with
                    | Some f', _ when f'.Name <> f.Name -> ok <- false
                    | _, Some w when w <> v -> ok <- false
                    | _ -> byKey <- byKey.Add((v, element), f); byField <- byField.Add(f.Name, v)
                // The cross-check: the machine declares a name's fields in the order its variables are
                // bound, so ordering the variables by binding orders their fields by declaration.
                let declared = machine.GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) |> Array.map (fun f -> f.Name) |> List.ofArray
                let ordered =
                    byKey
                    |> Map.toList
                    |> List.groupBy (fun ((v, _), f) -> v, (parse f.Name |> Option.bind snd).IsSome)
                    |> List.map (fun ((v, isElement), ks) -> (v, isElement), ks |> List.map (fun (_, f) -> List.findIndex ((=) f.Name) declared) |> List.min)
                    |> List.groupBy (fun ((v, isElement), _) -> v.Name, isElement)
                    |> List.forall (fun (_, vs) -> List.sortBy (fst >> fst >> bindingOrder) vs = List.sortBy snd vs)
                // Or, where the compiler declares fields in an order of its own (a tuple bound by a
                // pattern, split as it goes), the order their values were computed in (`storeOrder`).
                // The fill order, when it traces every field: Some whether it agrees, None when it
                // cannot tell (no filling method found, or a value not a local's or a parameter's).
                let flowed () =
                    match storeOrder machine (fun f -> (parse f).IsSome) with
                    | None -> None
                    | Some at ->
                        let groups =
                            byKey
                            |> Map.toList
                            |> List.groupBy (fun ((v, _), f) -> v, (parse f.Name |> Option.bind snd).IsSome)
                            |> List.map (fun ((v, isElement), ks) ->
                                (v, isElement), ks |> List.map (fun (_, f) -> match at.TryGetValue f.Name with | true, p -> p | _ -> None))
                        if groups |> List.exists (fun (_, ps) -> ps |> List.exists Option.isNone) then None
                        else
                            groups
                            |> List.map (fun (k, ps) -> k, ps |> List.choose id |> List.min)
                            |> List.groupBy (fun ((v, isElement), _) -> v.Name, isElement)
                            |> List.forall (fun (_, vs) ->
                                let byBinding = vs |> List.sortBy (fst >> fst >> bindingOrder) |> List.map snd
                                byBinding = List.sort byBinding && List.distinct byBinding = byBinding)
                            |> Some
                // The fill order decides when it traces every field (#210 review: the declaration
                // order can agree by coincidence with a pairing the optimizer's reordered reads
                // swapped); the declaration order only when it cannot.
                let agrees = match flowed () with Some flows -> flows | None -> ordered
                if ok && agrees then Some(byKey, Set.ofSeq assumed) else None
            | _ -> None
        attempt false |> Option.orElse (attempt true)
