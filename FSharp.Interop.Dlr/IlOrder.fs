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

    /// A method's instructions: each opcode with its 4-byte token when it has one.
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
                result.Add((op, (if size = 4 then BitConverter.ToInt32(il, i) else 0)))
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

    /// The quotation's events, in evaluation order: reads of variables named in `family`, and
    /// marker calls. A variable with no field is the optimizer's to inline: its recovered
    /// definition (`recover`) stands where it is read, a local function's body after its
    /// arguments. A builder call's lambdas are its loop and try bodies, inline in `MoveNext`.
    let private quotationEvents (isBuilder: Expr option -> bool) (hasField: Var -> bool) (recover: Var -> Expr option) (family: string -> bool) (body: Expr) : Event list option =
        let events = ResizeArray()
        let mutable ok = true
        let debug = not (isNull (Environment.GetEnvironmentVariable "DLR_IL_DEBUG"))
        let opaque = Collections.Generic.HashSet<Var>()
        let fail (why: string) (e: Expr) =
            if debug && ok then eprintfn "walk gives up (%s) at %s" why (let s = sprintf "%A" e in s.Substring(0, min 300 s.Length))
            ok <- false
        let rec lambdaBody (e: Expr) =
            match e with
            | Lambda(_, b) -> lambdaBody b
            | _ -> e
        /// The tuple variable of a shared name `e` is, seen through what the optimizer inlines: an
        /// alias, or a local function applied to unit whose body is it.
        let rec tupleOf (e: Expr) : Var option =
            match e with
            | Var v when family v.Name -> Some v
            | Var a when not (hasField a) ->
                match recover a with
                | Some(Var _ as d) -> tupleOf d
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
            | TupleGet(inner, i) when (tupleOf inner).IsSome -> events.Add(Read(box ((tupleOf inner).Value, Some i)))
            | Call(None, mi, [ inner ]) when mi.DeclaringType.FullName = "Microsoft.FSharp.Core.Operators" && (mi.Name = "Fst" || mi.Name = "Snd") && (tupleOf inner).IsSome ->
                events.Add(Read(box ((tupleOf inner).Value, Some(if mi.Name = "Fst" then 0 else 1))))
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
                    | Lambda _ -> walk depth (lambdaBody a)
                    | _ -> walk depth a
            | Call(target, mi, args) ->
                target |> Option.iter (walk depth)
                for a in args do walk depth a
                if isMarker mi then events.Add(Landmark mi.Name)
            // Binding forms: the generic shape gives their bodies as lambdas, which they are not.
            | Let(_, d, b) -> walk depth d; walk depth b
            | ForIntegerRangeLoop(_, a, b, body) -> walk depth a; walk depth b; walk depth body
            | TryWith(body, _, filter, _, handler) -> walk depth body; walk depth filter; walk depth handler
            | Lambda _ ->
                // A closure of its own: its reads are not in MoveNext.
                if (e.GetFreeVars() |> Seq.exists (fun v -> family v.Name)) then fail "nested lambda" e
            | ShapeVar _ -> ()
            | ShapeLambda _ -> ()
            | ShapeCombination(_, es) -> for x in es do walk depth x
        walk 0 body
        if ok then Some(List.ofSeq events) else None

    /// Each variable named in `names`, and the field of the machine it is, when the IL and the
    /// quotation agree; None otherwise.
    let resolve (machine: Type) (fields: Collections.Generic.IDictionary<string, FieldInfo>) (isBuilder: Expr option -> bool) (recover: Var -> Expr option) (memberBody: Expr) (names: Set<string>) (body: Expr) : Map<Var * int option, FieldInfo> option =
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
                | at when digits (rest.Substring(0, at)) && rest.Length > at + 1 && digits (rest.Substring(at + 1)) -> Some(n, Some(rest.Substring(at + 1)))
                | _ -> None)
        let hasField (v: Var) = fields.ContainsKey v.Name
        // Landmarks are dropped: an over-applied marker (`o?M(a, b)`) is a function value called
        // through FSharpFunc.InvokeFast in the IL, not a call to the marker.
        let reads events = events |> Option.map (List.choose (function Read r -> Some r | Landmark _ -> None))
        let ilR, qR = reads (ilEvents machine (fun f -> (parse f).IsSome)), reads (quotationEvents isBuilder hasField recover names.Contains body)
        if not (isNull (Environment.GetEnvironmentVariable "DLR_IL_DEBUG")) then
            eprintfn "IL %A\nQ %A" ilR (qR |> Option.map (List.map (fun r -> let (v: Var), (i: int option) = unbox r in sprintf "%s#%d%A" v.Name (v.GetHashCode()) i)))
        match ilR, qR with
        | Some il, Some q when il.Length = q.Length ->
            let fits (f: FieldInfo) (v: Var) (element: int option) =
                match element, parse f.Name with
                | None, Some(_, None) ->
                    f.FieldType = v.Type
                    || (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() = typedefof<Ref<_>> && f.FieldType.GetGenericArguments().[0] = v.Type)
                | Some i, Some(_, Some digits) ->
                    digits.StartsWith(string i) && Reflection.FSharpType.IsTuple v.Type
                    && f.FieldType = (Reflection.FSharpType.GetTupleElements v.Type).[i]
                | _ -> false
            let mutable byKey = Map.empty<Var * int option, FieldInfo>
            let mutable byField = Map.empty<string, Var * int option>
            let mutable ok = true
            for (fieldName, read) in List.zip il q do
                match fields.TryGetValue(unbox<string> fieldName) with
                | false, _ -> ok <- false
                | true, f ->
                let v, element = unbox<Var * int option> read
                if not (fits f v element) then ok <- false
                match byKey.TryFind(v, element), byField.TryFind f.Name with
                | Some f', _ when f'.Name <> f.Name -> ok <- false
                | _, Some k when k <> (v, element) -> ok <- false
                | _ -> byKey <- byKey.Add((v, element), f); byField <- byField.Add(f.Name, (v, element))
            // The cross-check: the machine declares a name's fields in the order its variables are
            // bound, so ordering the variables by binding orders their fields by declaration.
            let declared = machine.GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) |> Array.map (fun f -> f.Name) |> List.ofArray
            let ordered =
                byKey
                |> Map.toList
                |> List.groupBy (fun ((v, _), _) -> v)
                |> List.map (fun (v, ks) -> v, ks |> List.map (fun (_, f) -> List.findIndex ((=) f.Name) declared) |> List.min)
                |> List.groupBy (fun (v, _) -> v.Name)
                |> List.forall (fun (_, vs) -> List.sortBy (fst >> bindingOrder) vs = List.sortBy snd vs)
            if ok && ordered then Some byKey else None
        | _ -> None
