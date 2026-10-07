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

    /// The IL's events: loads of fields in `family`, and marker calls.
    let private ilEvents (machine: Type) (family: string -> bool) : Event list option =
        match machine.GetMethod("MoveNext", BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) with
        | null -> None
        | moveNext ->
        match moveNext.GetMethodBody() with
        | null -> None
        | body ->
        let il = body.GetILAsByteArray()
        let one, two = opCodes
        let typeArgs = if machine.IsGenericType then machine.GetGenericArguments() else [||]
        let events = ResizeArray()
        let mutable i = 0
        let mutable ok = true
        while ok && i < il.Length do
            let op =
                if il.[i] = 0xFEuy then (i <- i + 1; two.[int il.[i]]) else one.[int il.[i]]
            i <- i + 1
            match op with
            | None -> ok <- false
            | Some op ->
                let token () = BitConverter.ToInt32(il, i)
                match op.OperandType with
                | OperandType.InlineField when op = OpCodes.Ldfld || op = OpCodes.Ldflda ->
                    let f = moveNext.Module.ResolveField(token (), typeArgs, null)
                    if family f.Name && f.DeclaringType.Name = machine.Name then events.Add(Read(box f.Name))
                | OperandType.InlineMethod when op = OpCodes.Call || op = OpCodes.Callvirt ->
                    let m = moveNext.Module.ResolveMethod(token (), typeArgs, null)
                    if isMarker m then events.Add(Landmark m.Name)
                | _ -> ()
                i <- i +
                    match op.OperandType with
                    | OperandType.InlineNone -> 0
                    | OperandType.ShortInlineBrTarget | OperandType.ShortInlineI | OperandType.ShortInlineVar -> 1
                    | OperandType.InlineVar -> 2
                    | OperandType.InlineI8 | OperandType.InlineR -> 8
                    | OperandType.InlineSwitch -> 4 + 4 * BitConverter.ToInt32(il, i)
                    | _ -> 4
        if ok then Some(List.ofSeq events) else None

    /// The quotation's events, in evaluation order: reads of variables named in `family`, and
    /// marker calls. A variable with no field is the optimizer's to inline: its recovered
    /// definition (`recover`) stands where it is read, a local function's body after its
    /// arguments. A builder call's lambdas are its loop and try bodies, inline in `MoveNext`.
    let private quotationEvents (isBuilder: MethodInfo -> bool) (hasField: Var -> bool) (recover: Var -> Expr option) (family: string -> bool) (body: Expr) : Event list option =
        let events = ResizeArray()
        let mutable ok = true
        let rec lambdaBody (e: Expr) =
            match e with
            | Lambda(_, b) -> lambdaBody b
            | _ -> e
        let rec walk (depth: int) (e: Expr) =
            if not ok || depth > 64 then ok <- false
            else
            match e with
            | Var v when family v.Name -> events.Add(Read(box v))
            | Var v when not (hasField v) ->
                match recover v with
                | Some(Lambda _) | None -> ()
                | Some(LetRecursive _) -> ok <- false
                | Some def -> walk (depth + 1) def
            | VarSet(v, value) ->
                if family v.Name then events.Add(Read(box v))
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
                | Var fv when not (family fv.Name) && not (hasField fv) ->
                    match recover fv with
                    | Some(Lambda _ as def) ->
                        for a in args do walk depth a
                        walk (depth + 1) (lambdaBody def)
                    | _ -> ok <- false
                | Lambda _ ->
                    for a in args do walk depth a
                    walk depth (lambdaBody f)
                | _ ->
                    walk depth f
                    for a in args do walk depth a
            | Call(target, mi, args) when isBuilder mi ->
                target |> Option.iter (walk depth)
                for a in args do
                    match a with
                    | Lambda _ -> walk depth (lambdaBody a)
                    | _ -> walk depth a
            | Call(target, mi, args) ->
                target |> Option.iter (walk depth)
                for a in args do walk depth a
                if isMarker mi then events.Add(Landmark mi.Name)
            | Lambda _ ->
                // A closure of its own: its reads are not in MoveNext.
                if (e.GetFreeVars() |> Seq.exists (fun v -> family v.Name)) then ok <- false
            | ShapeVar _ -> ()
            | ShapeLambda _ -> ()
            | ShapeCombination(_, es) -> for x in es do walk depth x
        walk 0 body
        if ok then Some(List.ofSeq events) else None

    /// Each variable named in `names`, and the field of the machine it is, when the IL and the
    /// quotation agree; None otherwise.
    let resolve (machine: Type) (fields: Collections.Generic.IDictionary<string, FieldInfo>) (isBuilder: MethodInfo -> bool) (recover: Var -> Expr option) (memberBody: Expr) (names: Set<string>) (body: Expr) : Map<Var, FieldInfo> option =
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
        // `x`, `x0`, `x1`, … for each name.
        let familyOf (field: string) =
            names |> Seq.tryFind (fun n -> field = n || (field.StartsWith n && field.Length > n.Length && field.Substring(n.Length) |> Seq.forall Char.IsDigit))
        let hasField (v: Var) = fields.ContainsKey v.Name
        // Landmarks are dropped: an over-applied marker (`o?M(a, b)`) is a function value called
        // through FSharpFunc.InvokeFast in the IL, not a call to the marker.
        let reads events = events |> Option.map (List.filter (function Read _ -> true | Landmark _ -> false))
        match reads (ilEvents machine (fun f -> (familyOf f).IsSome)), reads (quotationEvents isBuilder hasField recover names.Contains body) with
        | Some il, Some q ->
            let segments (events: Event list) =
                let rec go current acc events =
                    match events with
                    | [] -> List.rev (List.rev current :: acc), []
                    | Landmark m :: rest ->
                        let segs, marks = go [] (List.rev current :: acc) rest
                        segs, m :: marks
                    | Read r :: rest -> go (r :: current) acc rest
                go [] [] events
            let ilSegs, ilMarks = segments il
            let qSegs, qMarks = segments q
            if ilMarks <> qMarks || ilSegs.Length <> qSegs.Length then None
            else
            let pairs =
                List.zip ilSegs qSegs
                |> List.map (fun (fs, vs) -> if List.length fs = List.length vs then Some(List.zip fs vs) else None)
            if pairs |> List.exists Option.isNone then None
            else
            let fits (f: FieldInfo) (v: Var) =
                f.FieldType = v.Type
                || (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() = typedefof<Ref<_>> && f.FieldType.GetGenericArguments().[0] = v.Type)
            let mutable byVar = Map.empty<Var, FieldInfo>
            let mutable byField = Map.empty<string, Var>
            let mutable ok = true
            for (fieldName, var) in pairs |> List.choose id |> List.concat do
                let f = fields.[unbox<string> fieldName]
                let v = unbox<Var> var
                if not (fits f v) then ok <- false
                match byVar.TryFind v, byField.TryFind f.Name with
                | Some f', _ when f'.Name <> f.Name -> ok <- false
                | _, Some v' when v' <> v -> ok <- false
                | _ -> byVar <- byVar.Add(v, f); byField <- byField.Add(f.Name, v)
            // The cross-check: the machine declares a name's fields in the order its variables are
            // bound, so the fields' declaration order must be the variables' binding order.
            let declared = machine.GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) |> Array.map (fun f -> f.Name) |> List.ofArray
            let ordered =
                byVar
                |> Map.toList
                |> List.groupBy (fun (v, _) -> v.Name)
                |> List.forall (fun (_, pairs) ->
                    let byBinding = pairs |> List.sortBy (fun (v, _) -> bindingOrder v) |> List.map (fun (_, f) -> f.Name)
                    let byDeclaration = byBinding |> List.sortBy (fun n -> List.findIndex ((=) n) declared)
                    byBinding = byDeclaration && List.forall (fun (_, (f: FieldInfo)) -> true) pairs)
            if ok && ordered then Some byVar else None
        | _ -> None
