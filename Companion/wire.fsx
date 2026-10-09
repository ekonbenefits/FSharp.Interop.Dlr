// The wire form of a block's member body, and its decoder (the run-time half): types and members
// by name, resolved by reflection against the loaded assemblies into an `Expr`.
module Wire

open System
open System.Collections.Generic
open System.Reflection
open FSharp.Quotations
open FSharp.Reflection

type TypeRef =
    /// Assembly simple name, CLI full name (nested with `+`), generic arguments.
    | TNamed of string * string * TypeRef list
    /// A generic parameter of the member or its type, by name.
    | TParam of string
    | TTuple of bool * TypeRef list
    | TArray of int * TypeRef
    | TByref of TypeRef

type MemberRef =
    { Declaring: TypeRef
      Name: string
      Instance: bool
      GenericArity: int
      Parameters: TypeRef list }

type VarDef = { Id: int; Name: string; Type: TypeRef; Mutable: bool }

type W =
    | WVar of int
    | WVarSet of int * W
    | WLambda of VarDef * W
    | WLet of VarDef * W * W
    | WLetRec of (VarDef * W) list * W
    | WApp of W * W
    | WConst of obj * TypeRef
    | WDefault of TypeRef
    | WCall of W option * MemberRef * TypeRef list * TypeRef list * W list
    /// A call with trait witnesses: the method's `$W` twin takes them first.
    | WCallW of W option * MemberRef * TypeRef list * TypeRef list * W list * W list
    /// A resolved trait call: the source type, the member's name, the target, argument types.
    | WTraitCall of TypeRef * string * W option * TypeRef list * W list
    | WNewObject of MemberRef * TypeRef list * W list
    | WStaticValue of TypeRef * string
    | WNewRecord of TypeRef * W list
    | WNewUnion of TypeRef * string * W list
    | WUnionTest of W * TypeRef * string
    | WUnionGet of W * TypeRef * string * int
    | WNewTuple of TypeRef * W list
    | WTupleGet of TypeRef * int * W
    | WFieldGet of W option * TypeRef * string
    | WFieldSet of W option * TypeRef * string * W
    | WIf of W * W * W
    | WSeq of W * W
    | WWhile of W * W
    | WFor of VarDef * W * W * W
    | WTryWith of W * VarDef * W * VarDef * W
    | WTryFinally of W * W
    | WCoerce of TypeRef * W
    | WTypeTest of TypeRef * W
    | WNewArray of TypeRef * W list
    | WNewDelegate of TypeRef * VarDef list * W
    | WQuote of W
    | WUnsupported of string

exception Unsupported of string

let all = BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic

/// Generic parameters in scope, by name.
type Scope = IDictionary<string, Type>

let private assemblies = Dictionary<string, Assembly>()
let assemblyNamed (name: string) =
    match assemblies.TryGetValue name with
    | true, a -> a
    | _ ->
        let a =
            AppDomain.CurrentDomain.GetAssemblies() |> Array.tryFind (fun a -> a.GetName().Name = name)
            |> Option.defaultWith (fun () -> Assembly.Load(AssemblyName name))
        assemblies.[name] <- a
        a

let typeDefinition (asm: string) (name: string) : Type =
    let a = assemblyNamed asm
    match a.GetType(name) with
    | null ->
        // Forwarded (System.Runtime → System.Private.CoreLib) or a reference assembly's name.
        match Type.GetType(name + ", " + asm) with
        | null ->
            match Type.GetType name with
            | null -> raise (Unsupported(sprintf "type %s in %s" name asm))
            | t -> t
        | t -> t
    | t -> t

let rec resolve (scope: Scope) (t: TypeRef) : Type =
    match t with
    | TParam n ->
        match scope.TryGetValue n with
        | true, t -> t
        | _ -> raise (Unsupported(sprintf "generic parameter %s" n))
    | TTuple(false, ts) -> FSharpType.MakeTupleType [| for t in ts -> resolve scope t |]
    | TTuple(true, ts) -> FSharpType.MakeStructTupleType(typeof<int>.Assembly, [| for t in ts -> resolve scope t |])
    | TArray(1, e) -> (resolve scope e).MakeArrayType()
    | TArray(r, e) -> (resolve scope e).MakeArrayType r
    | TByref e -> (resolve scope e).MakeByRefType()
    | TNamed(asm, name, []) -> typeDefinition asm name
    | TNamed(asm, name, args) -> (typeDefinition asm name).MakeGenericType [| for a in args -> resolve scope a |]

/// Does the wire type describe `t`, a parameter type of a generic definition?
let rec matches (r: TypeRef) (t: Type) =
    match r with
    | TParam n -> t.IsGenericParameter && t.Name = n
    | TByref e -> t.IsByRef && matches e (t.GetElementType())
    | TArray(rank, e) -> t.IsArray && t.GetArrayRank() = rank && matches e (t.GetElementType())
    | TTuple(_, ts) ->
        // Up to seven elements (a longer tuple nests; not matched here).
        t.IsGenericType && t.GetGenericTypeDefinition().Name.Contains "Tuple`"
        && (let args = t.GetGenericArguments() in args.Length = ts.Length && List.forall2 matches ts (List.ofArray args))
    | TNamed(_, name, args) ->
        if t.IsGenericParameter || t.IsArray || t.IsByRef then false
        elif t.IsGenericType then
            let d = t.GetGenericTypeDefinition()
            d.FullName = name && (let a = t.GetGenericArguments() in a.Length = args.Length && List.forall2 matches args (List.ofArray a))
        else (t.FullName = name || t.Name = name) && args.IsEmpty

let methodOf (scope: Scope) (m: MemberRef) (typeArgs: TypeRef list) (methodArgs: TypeRef list) : MethodBase =
    let asm, name = match m.Declaring with TNamed(a, n, _) -> a, n | other -> raise (Unsupported(sprintf "declaring %A" other))
    let def = typeDefinition asm name
    let candidates =
        [ for c in Seq.append (def.GetMethods all |> Seq.cast<MethodBase>) (def.GetConstructors all |> Seq.cast<MethodBase>) do
            let arity = if c.IsGenericMethodDefinition then c.GetGenericArguments().Length else 0
            let ps = c.GetParameters()
            if c.Name = m.Name && arity = m.GenericArity && ps.Length = m.Parameters.Length && c.IsStatic = not m.Instance then
                c ]
    let chosen =
        match candidates with
        | [ c ] -> c
        | [] -> raise (Unsupported(sprintf "no member %s.%s/%d (%d params)" name m.Name m.GenericArity m.Parameters.Length))
        | many ->
            match many |> List.filter (fun c -> List.forall2 matches m.Parameters [ for p in c.GetParameters() -> p.ParameterType ]) with
            | [ c ] -> c
            // A member that hides an inherited one of the same signature (Exception.GetType).
            | fits when (fits |> List.filter (fun c -> c.DeclaringType = def)).Length = 1 -> fits |> List.find (fun c -> c.DeclaringType = def)
            | _ -> raise (Unsupported(sprintf "ambiguous %s.%s" name m.Name))
    let onType =
        if typeArgs.IsEmpty then chosen
        else
            let inst = def.MakeGenericType [| for a in typeArgs -> resolve scope a |]
            MethodBase.GetMethodFromHandle(chosen.MethodHandle, inst.TypeHandle)
    match onType with
    | :? MethodInfo as mi when mi.IsGenericMethodDefinition -> mi.MakeGenericMethod [| for a in methodArgs -> resolve scope a |] :> MethodBase
    | other -> other

let propertyOf (mi: MethodInfo) =
    mi.DeclaringType.GetProperties all
    |> Array.tryFind (fun p ->
        let g = p.GetGetMethod true
        let s = p.GetSetMethod true
        (not (isNull g) && g.MethodHandle = mi.MethodHandle) || (not (isNull s) && s.MethodHandle = mi.MethodHandle))

/// A combination node built without FSharp.Core's type check, as its own decoder builds stored
/// quotations: a delegate of no arguments over `fun () -> …` keeps the unit parameter, which
/// `Expr.NewDelegate` refuses (and it refuses any `Action` of no arguments).
let rawNewDelegate (t: Type) (lambda: Expr) : Expr =
    let core = typeof<Expr>.Assembly
    let info = core.GetType "Microsoft.FSharp.Quotations.ExprConstInfo"
    let tree = core.GetType "Microsoft.FSharp.Quotations.Tree"
    let case (u: Type) name = FSharp.Reflection.FSharpType.GetUnionCases(u, true) |> Array.find (fun c -> c.Name = name)
    let op = FSharp.Reflection.FSharpValue.MakeUnion(case info "NewDelegateOp", [| box t |], true)
    let comb = FSharp.Reflection.FSharpValue.MakeUnion(case tree "CombTerm", [| op; box [ lambda ] |], true)
    let ctor = typeof<Expr>.GetConstructors(BindingFlags.NonPublic ||| BindingFlags.Instance) |> Array.head
    ctor.Invoke [| comb; box ([]: Expr list) |] :?> Expr

let decode (scope: Scope) (w: W) : Expr =
    let vars = Dictionary<int, Var>()
    let def (d: VarDef) =
        let v = Var(d.Name, resolve scope d.Type, d.Mutable)
        vars.[d.Id] <- v
        v
    let rec go (w: W) : Expr =
        try go1 w
        with :? ArgumentException as e -> raise (Unsupported(sprintf "%s: %s" ((sprintf "%A" w).Split([| ' '; '\n' |]).[0]) (e.Message.Split('\n').[0])))
    and go1 (w: W) : Expr =
        match w with
        | WVar id -> Expr.Var vars.[id]
        | WVarSet(id, e) -> Expr.VarSet(vars.[id], go e)
        | WLambda(d, b) -> let v = def d in Expr.Lambda(v, go b)
        | WLet(d, e, b) ->
            let e = go e
            let v = def d
            Expr.Let(v, e, go b)
        | WLetRec(bs, b) ->
            let vs = [ for (d, _) in bs -> def d ]
            Expr.LetRecursive([ for v, (_, e) in List.zip vs bs -> v, go e ], go b)
        | WApp(f, a) -> Expr.Application(go f, go a)
        | WConst(v, t) ->
            let t = resolve scope t
            Expr.Value((if t.IsEnum && not (isNull v) then Enum.ToObject(t, v) else v), t)
        | WDefault t ->
            let t = resolve scope t
            if t.IsValueType then Expr.DefaultValue t else Expr.Value(null, t)
        | WCall(o, m, targs, margs, args) ->
            let o = Option.map go o
            let args = List.map go args
            match methodOf scope m targs margs with
            | :? ConstructorInfo as c -> Expr.NewObject(c, args)
            | :? MethodInfo as mi when mi.IsSpecialName && (mi.Name.StartsWith "get_" || mi.Name.StartsWith "set_") ->
                match propertyOf mi with
                | Some p when mi.Name.StartsWith "get_" ->
                    match o with
                    | Some o -> Expr.PropertyGet(o, p, args)
                    | None -> Expr.PropertyGet(p, args)
                | Some p ->
                    let idx, v = List.take (args.Length - 1) args, List.last args
                    match o with
                    | Some o -> Expr.PropertySet(o, p, v, idx)
                    | None -> Expr.PropertySet(p, v, idx)
                | None ->
                    match o with
                    | Some o -> Expr.Call(o, mi, args)
                    | None -> Expr.Call(mi, args)
            | :? MethodInfo as mi ->
                try
                    match o with
                    | Some o -> Expr.Call(o, mi, args)
                    | None -> Expr.Call(mi, args)
                with :? ArgumentException as e -> raise (Unsupported(sprintf "call %s.%s: %d args for %d params: %s" mi.DeclaringType.Name mi.Name args.Length (mi.GetParameters().Length) (e.Message.Split('\n').[0])))
            | _ -> raise (Unsupported "method kind")
        | WCallW(o, m, targs, margs, ws, args) ->
            let o = Option.map go o
            let ws = List.map go ws
            let args = List.map go args
            let mi = methodOf scope m targs margs :?> MethodInfo
            let miw = methodOf scope { m with Name = m.Name + "$W"; Parameters = [ for _ in ws -> TParam "?" ] @ m.Parameters } targs margs :?> MethodInfo
            match o with
            | Some o -> Expr.CallWithWitnesses(o, mi, miw, ws, args)
            | None -> Expr.CallWithWitnesses(mi, miw, ws, args)
        | WTraitCall(t, name, o, argTypes, args) ->
            let t = resolve scope t
            let types = [| for a in argTypes -> resolve scope a |]
            let mi =
                match t.GetMethod(name, all, null, types, null) with
                | null -> raise (Unsupported(sprintf "trait %s.%s" t.Name name))
                | mi -> mi
            match o with
            | Some o -> Expr.Call(go o, mi, List.map go args)
            | None -> Expr.Call(mi, List.map go args)
        | WNewObject(m, targs, args) ->
            match methodOf scope m targs [] with
            | :? ConstructorInfo as c -> Expr.NewObject(c, List.map go args)
            | _ -> raise (Unsupported "constructor")
        | WStaticValue(t, name) ->
            let t = resolve scope t
            match t.GetProperty(name, all) with
            | null -> raise (Unsupported(sprintf "module value %s.%s" t.Name name))
            | p -> Expr.PropertyGet p
        | WNewRecord(t, args) -> Expr.NewRecord(resolve scope t, List.map go args)
        | WNewUnion(t, case, args) ->
            let c = FSharpType.GetUnionCases(resolve scope t, true) |> Array.find (fun c -> c.Name = case)
            Expr.NewUnionCase(c, List.map go args)
        | WUnionTest(e, t, case) ->
            let c = FSharpType.GetUnionCases(resolve scope t, true) |> Array.find (fun c -> c.Name = case)
            Expr.UnionCaseTest(go e, c)
        | WUnionGet(e, t, case, i) ->
            let c = FSharpType.GetUnionCases(resolve scope t, true) |> Array.find (fun c -> c.Name = case)
            Expr.PropertyGet(go e, c.GetFields().[i])
        | WNewTuple(t, args) ->
            match t with
            | TTuple(true, _) -> Expr.NewStructTuple(typeof<int>.Assembly, List.map go args)
            | _ -> Expr.NewTuple(List.map go args)
        | WTupleGet(_, i, e) -> Expr.TupleGet(go e, i)
        | WFieldGet(o, t, name) ->
            let t = resolve scope t
            match t.GetProperty(name, all), t.GetField(name, all) with
            | p, _ when not (isNull p) && FSharpType.IsRecord(t, true) ->
                match o with
                | Some o -> Expr.PropertyGet(go o, p)
                | None -> Expr.PropertyGet p
            | _, f when not (isNull f) ->
                match o with
                | Some o -> Expr.FieldGet(go o, f)
                | None -> Expr.FieldGet f
            | _ -> raise (Unsupported(sprintf "field %s.%s" t.Name name))
        | WFieldSet(o, t, name, v) ->
            let t = resolve scope t
            match t.GetProperty(name, all), t.GetField(name, all) with
            | p, _ when not (isNull p) && FSharpType.IsRecord(t, true) ->
                match o with
                | Some o -> Expr.PropertySet(go o, p, go v)
                | None -> Expr.PropertySet(p, go v)
            | _, f when not (isNull f) ->
                match o with
                | Some o -> Expr.FieldSet(go o, f, go v)
                | None -> Expr.FieldSet(f, go v)
            | _ -> raise (Unsupported(sprintf "field %s.%s" t.Name name))
        | WIf(c, a, b) -> Expr.IfThenElse(go c, go a, go b)
        | WSeq(a, b) -> Expr.Sequential(go a, go b)
        | WWhile(c, b) -> Expr.WhileLoop(go c, go b)
        | WFor(d, lo, hi, b) ->
            let lo, hi = go lo, go hi
            let v = def d
            Expr.ForIntegerRangeLoop(v, lo, hi, go b)
        | WTryWith(b, fv, f, cv, c) ->
            let b = go b
            let fv = def fv
            let f = go f
            let cv = def cv
            Expr.TryWith(b, fv, f, cv, go c)
        | WTryFinally(b, f) -> Expr.TryFinally(go b, go f)
        | WCoerce(t, e) -> Expr.Coerce(go e, resolve scope t)
        | WTypeTest(t, e) -> Expr.TypeTest(go e, resolve scope t)
        | WNewArray(t, es) -> Expr.NewArray(resolve scope t, List.map go es)
        | WNewDelegate(t, ds, b) ->
            let t = resolve scope t
            let vs = List.map def ds
            let invoke = t.GetMethod("Invoke", all)
            if invoke.GetParameters().Length = 0 && vs.IsEmpty then
                // `Action(ignore)`: the quotation holds the body itself, no lambda.
                rawNewDelegate t (go b)
            elif invoke.GetParameters().Length = 0 && vs.Length = 1 then
                // A delegate of no arguments over `fun () -> …`: the quotation keeps the unit
                // parameter, which Expr.NewDelegate's check refuses; rebuild the node around it.
                rawNewDelegate t (Expr.Lambda(vs.Head, go b))
            else
                let b = go b
                try Expr.NewDelegate(t, vs, b)
                with :? ArgumentException as e -> raise (Unsupported(sprintf "delegate %s: %d params, vars %A, body %s" t.Name (invoke.GetParameters().Length) [ for v in vs -> v.Type.Name ] b.Type.Name))
        | WQuote e -> Expr.Quote(go e)
        | WUnsupported s -> raise (Unsupported s)
    let go' = go
    try go' w with :? ArgumentException as e -> raise e
