/// Block bodies from the build companion's map (`body` sections of `FSharp.Interop.Dlr.CaptureMap`):
/// the member holding a dlr { } block, from the compiler's unoptimized tree, in a wire form that names
/// types and members; decoded by reflection into the `Expr` its [<ReflectedDefinition>] would be, so
/// a block needs no attribute when the map has it. `Companion/bodies.fsx` writes it (and #loads this
/// file for the writer).
#if INTERACTIVE
module FSharp.Interop.Dlr.BodyMap
#else
module internal FSharp.Interop.Dlr.BodyMap
#endif

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Reflection
open System.Text
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
      Parameters: TypeRef list
      /// Tells apart overloads that differ only by it (`op_Explicit`).
      Return: TypeRef }

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
    | WStaticSet of TypeRef * string * W
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
    /// Raw (`<@@ @@>`) or typed.
    | WQuote of bool * W
    | WUnsupported of string

exception Unsupported of string

// ---- Text: a prefix token stream, tokens separated by a space; a string is `length:chars` the
// first time and `#n` (the n-th string) after, since type and member names repeat.

type Writer() =
    let sb = StringBuilder()
    let strings = Dictionary<string, int>()
    member _.Token(s: string) = sb.Append(s).Append(' ') |> ignore
    member w.Int(i: int) = w.Token(string i)
    member _.Str(s: string) =
        match strings.TryGetValue s with
        | true, n -> sb.Append('#').Append(n).Append(' ') |> ignore
        | _ ->
            strings.[s] <- strings.Count
            sb.Append(s.Length).Append(':').Append(s).Append(' ') |> ignore
    member w.List(xs: 'a list, f: 'a -> unit) = w.Int xs.Length; List.iter f xs
    member w.Option(x: 'a option, f: 'a -> unit) = match x with Some x -> w.Token "1"; f x | None -> w.Token "0"
    override _.ToString() = sb.ToString()

type Reader(s: string) =
    let mutable i = 0
    let strings = ResizeArray<string>()
    member _.Token() =
        let j = s.IndexOf(' ', i)
        let t = s.Substring(i, j - i)
        i <- j + 1
        t
    member r.Int() = int (r.Token())
    member r.Str() =
        if s.[i] = '#' then
            i <- i + 1
            strings.[r.Int()]
        else
            let colon = s.IndexOf(':', i)
            let n = int (s.Substring(i, colon - i))
            let t = s.Substring(colon + 1, n)
            i <- colon + 1 + n + 1
            strings.Add t
            t
    member r.List(f: unit -> 'a) = let n = r.Int() in List.init n (fun _ -> f ())
    member r.Option(f: unit -> 'a) = if r.Token() = "1" then Some(f ()) else None

let rec writeType (w: Writer) (t: TypeRef) =
    match t with
    | TNamed(a, n, args) -> w.Token "N"; w.Str a; w.Str n; w.List(args, writeType w)
    | TParam n -> w.Token "P"; w.Str n
    | TTuple(s, ts) -> w.Token "T"; w.Int(if s then 1 else 0); w.List(ts, writeType w)
    | TArray(r, e) -> w.Token "A"; w.Int r; writeType w e
    | TByref e -> w.Token "R"; writeType w e

let rec readType (r: Reader) : TypeRef =
    match r.Token() with
    | "N" -> let a = r.Str() in let n = r.Str() in TNamed(a, n, r.List(fun () -> readType r))
    | "P" -> TParam(r.Str())
    | "T" -> let s = r.Int() = 1 in TTuple(s, r.List(fun () -> readType r))
    | "A" -> let rank = r.Int() in TArray(rank, readType r)
    | "R" -> TByref(readType r)
    | t -> raise (Unsupported("type tag " + t))

let writeMember (w: Writer) (m: MemberRef) =
    writeType w m.Declaring; w.Str m.Name; w.Int(if m.Instance then 1 else 0); w.Int m.GenericArity; w.List(m.Parameters, writeType w); writeType w m.Return

let readMember (r: Reader) : MemberRef =
    let d = readType r
    let n = r.Str()
    let inst = r.Int() = 1
    let arity = r.Int()
    let ps = r.List(fun () -> readType r)
    { Declaring = d; Name = n; Instance = inst; GenericArity = arity; Parameters = ps; Return = readType r }

let writeVar (w: Writer) (d: VarDef) = w.Int d.Id; w.Str d.Name; writeType w d.Type; w.Int(if d.Mutable then 1 else 0)
let readVar (r: Reader) : VarDef =
    let id = r.Int()
    let n = r.Str()
    let t = readType r
    { Id = id; Name = n; Type = t; Mutable = r.Int() = 1 }

/// A constant: its type code and invariant text (a char by its code, so any char survives).
let writeConst (w: Writer) (v: obj) =
    match v with
    | null -> w.Token "null"
    | :? char as c -> w.Token "Char"; w.Int(int c)
    | :? string as s -> w.Token "String"; w.Str s
    | :? float as f -> w.Token "Double"; w.Str(f.ToString("R", CultureInfo.InvariantCulture))
    | :? float32 as f -> w.Token "Single"; w.Str(f.ToString("R", CultureInfo.InvariantCulture))
    | v -> w.Token(Type.GetTypeCode(v.GetType()).ToString()); w.Str(Convert.ToString(v, CultureInfo.InvariantCulture))

let readConst (r: Reader) : obj =
    match r.Token() with
    | "null" -> null
    | "Char" -> box (char (r.Int()))
    | "String" -> box (r.Str())
    | code -> Convert.ChangeType(r.Str(), (Enum.Parse(typeof<TypeCode>, code) :?> TypeCode), CultureInfo.InvariantCulture)

let rec write (w: Writer) (e: W) =
    let go = write w
    let ty = writeType w
    match e with
    | WVar id -> w.Token "v"; w.Int id
    | WVarSet(id, x) -> w.Token "vs"; w.Int id; go x
    | WLambda(d, b) -> w.Token "fn"; writeVar w d; go b
    | WLet(d, x, b) -> w.Token "let"; writeVar w d; go x; go b
    | WLetRec(bs, b) -> w.Token "rec"; w.List(bs, fun (d, x) -> writeVar w d; go x); go b
    | WApp(f, x) -> w.Token "app"; go f; go x
    | WConst(v, t) -> w.Token "k"; writeConst w v; ty t
    | WDefault t -> w.Token "def"; ty t
    | WCall(o, m, ta, ma, xs) -> w.Token "call"; w.Option(o, go); writeMember w m; w.List(ta, ty); w.List(ma, ty); w.List(xs, go)
    | WCallW(o, m, ta, ma, ws, xs) -> w.Token "callw"; w.Option(o, go); writeMember w m; w.List(ta, ty); w.List(ma, ty); w.List(ws, go); w.List(xs, go)
    | WTraitCall(t, n, o, ts, xs) -> w.Token "trait"; ty t; w.Str n; w.Option(o, go); w.List(ts, ty); w.List(xs, go)
    | WNewObject(m, ta, xs) -> w.Token "new"; writeMember w m; w.List(ta, ty); w.List(xs, go)
    | WStaticValue(t, n) -> w.Token "sv"; ty t; w.Str n
    | WStaticSet(t, n, x) -> w.Token "sv="; ty t; w.Str n; go x
    | WNewRecord(t, xs) -> w.Token "rec{"; ty t; w.List(xs, go)
    | WNewUnion(t, c, xs) -> w.Token "case"; ty t; w.Str c; w.List(xs, go)
    | WUnionTest(x, t, c) -> w.Token "case?"; go x; ty t; w.Str c
    | WUnionGet(x, t, c, i) -> w.Token "case."; go x; ty t; w.Str c; w.Int i
    | WNewTuple(t, xs) -> w.Token "tup"; ty t; w.List(xs, go)
    | WTupleGet(t, i, x) -> w.Token "tup."; ty t; w.Int i; go x
    | WFieldGet(o, t, n) -> w.Token "fld"; w.Option(o, go); ty t; w.Str n
    | WFieldSet(o, t, n, x) -> w.Token "fld="; w.Option(o, go); ty t; w.Str n; go x
    | WIf(c, a, b) -> w.Token "if"; go c; go a; go b
    | WSeq(a, b) -> w.Token "seq"; go a; go b
    | WWhile(c, b) -> w.Token "while"; go c; go b
    | WFor(d, lo, hi, b) -> w.Token "for"; writeVar w d; go lo; go hi; go b
    | WTryWith(b, fd, f, cd, c) -> w.Token "try"; go b; writeVar w fd; go f; writeVar w cd; go c
    | WTryFinally(b, f) -> w.Token "finally"; go b; go f
    | WCoerce(t, x) -> w.Token ":>"; ty t; go x
    | WTypeTest(t, x) -> w.Token ":?"; ty t; go x
    | WNewArray(t, xs) -> w.Token "arr"; ty t; w.List(xs, go)
    | WNewDelegate(t, ds, b) -> w.Token "del"; ty t; w.List(ds, writeVar w); go b
    | WQuote(raw, x) -> w.Token "quote"; w.Int(if raw then 1 else 0); go x
    | WUnsupported s -> w.Token "unsupported"; w.Str s

let rec read (r: Reader) : W =
    let go () = read r
    let ty () = readType r
    match r.Token() with
    | "v" -> WVar(r.Int())
    | "vs" -> let id = r.Int() in WVarSet(id, go ())
    | "fn" -> let d = readVar r in WLambda(d, go ())
    | "let" -> let d = readVar r in let x = go () in WLet(d, x, go ())
    | "rec" -> let bs = r.List(fun () -> let d = readVar r in d, go ()) in WLetRec(bs, go ())
    | "app" -> let f = go () in WApp(f, go ())
    | "k" -> let v = readConst r in WConst(v, ty ())
    | "def" -> WDefault(ty ())
    | "call" ->
        let o = r.Option go
        let m = readMember r
        let ta = r.List ty
        let ma = r.List ty
        WCall(o, m, ta, ma, r.List go)
    | "callw" ->
        let o = r.Option go
        let m = readMember r
        let ta = r.List ty
        let ma = r.List ty
        let ws = r.List go
        WCallW(o, m, ta, ma, ws, r.List go)
    | "trait" ->
        let t = ty ()
        let n = r.Str()
        let o = r.Option go
        let ts = r.List ty
        WTraitCall(t, n, o, ts, r.List go)
    | "new" -> let m = readMember r in let ta = r.List ty in WNewObject(m, ta, r.List go)
    | "sv" -> let t = ty () in WStaticValue(t, r.Str())
    | "sv=" -> let t = ty () in let n = r.Str() in WStaticSet(t, n, go ())
    | "rec{" -> let t = ty () in WNewRecord(t, r.List go)
    | "case" -> let t = ty () in let c = r.Str() in WNewUnion(t, c, r.List go)
    | "case?" -> let x = go () in let t = ty () in WUnionTest(x, t, r.Str())
    | "case." -> let x = go () in let t = ty () in let c = r.Str() in WUnionGet(x, t, c, r.Int())
    | "tup" -> let t = ty () in WNewTuple(t, r.List go)
    | "tup." -> let t = ty () in let i = r.Int() in WTupleGet(t, i, go ())
    | "fld" -> let o = r.Option go in let t = ty () in WFieldGet(o, t, r.Str())
    | "fld=" -> let o = r.Option go in let t = ty () in let n = r.Str() in WFieldSet(o, t, n, go ())
    | "if" -> let c = go () in let a = go () in WIf(c, a, go ())
    | "seq" -> let a = go () in WSeq(a, go ())
    | "while" -> let c = go () in WWhile(c, go ())
    | "for" -> let d = readVar r in let lo = go () in let hi = go () in WFor(d, lo, hi, go ())
    | "try" -> let b = go () in let fd = readVar r in let f = go () in let cd = readVar r in WTryWith(b, fd, f, cd, go ())
    | "finally" -> let b = go () in WTryFinally(b, go ())
    | ":>" -> let t = ty () in WCoerce(t, go ())
    | ":?" -> let t = ty () in WTypeTest(t, go ())
    | "arr" -> let t = ty () in WNewArray(t, r.List go)
    | "del" -> let t = ty () in let ds = r.List(fun () -> readVar r) in WNewDelegate(t, ds, go ())
    | "quote" -> let raw = r.Int() = 1 in WQuote(raw, go ())
    | "unsupported" -> WUnsupported(r.Str())
    | t -> raise (Unsupported("node tag " + t))

let all = BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic

/// Generic parameters in scope, by name.
type Scope = IDictionary<string, Type>

let private assemblies = ConcurrentDictionary<string, Assembly>()
let assemblyNamed (name: string) =
    assemblies.GetOrAdd(name, fun name ->
        AppDomain.CurrentDomain.GetAssemblies() |> Array.tryFind (fun a -> a.GetName().Name = name)
        |> Option.defaultWith (fun () -> Assembly.Load(AssemblyName name)))

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
        // A parameter the block's closure does not carry is unused by the block: any will do
        // (as Discover instantiates a generic member's reflected definition).
        | _ -> typeof<obj>
    | TTuple(false, ts) -> FSharpType.MakeTupleType [| for t in ts -> resolve scope t |]
    | TTuple(true, ts) -> FSharpType.MakeStructTupleType(typeof<int>.Assembly, [| for t in ts -> resolve scope t |])
    | TArray(1, e) -> (resolve scope e).MakeArrayType()
    | TArray(r, e) -> (resolve scope e).MakeArrayType r
    | TByref e -> (resolve scope e).MakeByRefType()
    | TNamed(asm, name, []) -> typeDefinition asm name
    | TNamed(asm, name, args) -> (typeDefinition asm name).MakeGenericType [| for a in args -> resolve scope a |]

/// Does the wire type describe `t`, a parameter type of a generic definition?
/// Does the wire type describe `t`, a parameter or return type of a generic definition? Exact:
/// a member is bound only when its whole signature is the one the compiler resolved.
let rec matches (r: TypeRef) (t: Type) =
    match r with
    // A `$W` twin's witness parameter, whose type the wire form does not name.
    | TParam "?" -> true
    // A generic parameter by position: `!!i` the method's, `!i` the type's.
    | TParam n when n.StartsWith "!!" -> t.IsGenericParameter && not (isNull t.DeclaringMethod) && t.GenericParameterPosition = int (n.Substring 2)
    | TParam n when n.StartsWith "!" -> t.IsGenericParameter && isNull t.DeclaringMethod && t.GenericParameterPosition = int (n.Substring 1)
    | TParam n -> t.IsGenericParameter && t.Name = n
    | TByref e -> t.IsByRef && matches e (t.GetElementType())
    | TArray(rank, e) -> t.IsArray && t.GetArrayRank() = rank && matches e (t.GetElementType())
    | TTuple(isStruct, ts) ->
        // FSharpType reads a long tuple's nested rest as more elements.
        FSharpType.IsTuple t && t.IsValueType = isStruct
        && (let es = FSharpType.GetTupleElements t in es.Length = ts.Length && List.forall2 matches ts (List.ofArray es))
    | TNamed(_, name, args) ->
        if t.IsGenericParameter || t.IsArray || t.IsByRef then false
        elif t.IsGenericType then
            let d = t.GetGenericTypeDefinition()
            d.FullName = name && (let a = t.GetGenericArguments() in a.Length = args.Length && List.forall2 matches args (List.ofArray a))
        else t.FullName = name && args.IsEmpty

let returns (r: TypeRef) (c: MethodBase) =
    match c with
    | :? MethodInfo as mi -> matches r mi.ReturnType || (r = TNamed("FSharp.Core", "Microsoft.FSharp.Core.Unit", []) && mi.ReturnType = typeof<Void>)
    | _ -> false

let methodOf (scope: Scope) (m: MemberRef) (typeArgs: TypeRef list) (methodArgs: TypeRef list) : MethodBase =
    let asm, name = match m.Declaring with TNamed(a, n, _) -> a, n | other -> raise (Unsupported(sprintf "declaring %A" other))
    let def = typeDefinition asm name
    let candidates =
        [ for c in Seq.append (def.GetMethods all |> Seq.cast<MethodBase>) (def.GetConstructors all |> Seq.cast<MethodBase>) do
            let arity = if c.IsGenericMethodDefinition then c.GetGenericArguments().Length else 0
            let ps = c.GetParameters()
            if c.Name = m.Name && arity = m.GenericArity && ps.Length = m.Parameters.Length && c.IsStatic = not m.Instance then
                c ]
    // The whole signature, even for a lone candidate: an assembly that differs at run time from the
    // one compiled against must not bind another member of the same shape.
    let fits =
        candidates |> List.filter (fun c ->
            List.forall2 matches m.Parameters [ for p in c.GetParameters() -> p.ParameterType ]
            && (match c with :? MethodInfo -> returns m.Return c | _ -> true))
    let chosen =
        match fits with
        | [ c ] -> c
        | [] -> raise (Unsupported(sprintf "no member %s.%s/%d with its signature (%d of the name and shape)" name m.Name m.GenericArity candidates.Length))
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
        | WStaticSet(t, name, v) ->
            let t = resolve scope t
            match t.GetProperty(name, all) with
            | null -> raise (Unsupported(sprintf "module value %s.%s" t.Name name))
            | p -> Expr.PropertySet(p, go v)
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
            // Built without FSharp.Core's check below, so checked here: the body has Invoke's
            // return type (unit for void).
            let returnsAs (b: Expr) =
                let expected = if invoke.ReturnType = typeof<Void> then typeof<unit> else invoke.ReturnType
                if b.Type <> expected then raise (Unsupported(sprintf "delegate %s: body of type %s" t.Name b.Type.Name))
                b
            if invoke.GetParameters().Length = 0 && vs.IsEmpty then
                // `Action(ignore)`: the quotation holds the body itself, no lambda.
                rawNewDelegate t (returnsAs (go b))
            elif invoke.GetParameters().Length = 0 && vs.Length = 1 && vs.Head.Type = typeof<unit> then
                // A delegate of no arguments over `fun () -> …`: the quotation keeps the unit
                // parameter, which Expr.NewDelegate's check refuses; rebuild the node around it.
                rawNewDelegate t (Expr.Lambda(vs.Head, returnsAs (go b)))
            else
                let b = go b
                try Expr.NewDelegate(t, vs, b)
                with :? ArgumentException -> raise (Unsupported(sprintf "delegate %s: %d params, vars %A, body %s" t.Name (invoke.GetParameters().Length) [ for v in vs -> v.Type.Name ] b.Type.Name))
        | WQuote(true, e) -> Expr.QuoteRaw(go e)
        | WQuote(false, e) -> Expr.QuoteTyped(go e)
        | WUnsupported s -> raise (Unsupported s)
    go w

// ---- The map's `body` section (format 2, read through `CaptureMap.body`): per block, `X <payload>`,
// the type declaring the member (the binder's accessibility context) and the member's tree, as text
// with `\`, tab, LF and CR escaped (`\\`, `\t`, `\n`, `\r`) so it stays one line; or `Y file line`,
// the block in the same file whose section holds its member; or `A n`, a line where n members have a
// block (refused, as from reflected definitions). The map compresses per source file.

let private escape (s: string) =
    let sb = StringBuilder(s.Length)
    for c in s do
        match c with
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | c -> sb.Append c |> ignore
    sb.ToString()

let private unescape (s: string) =
    if s.IndexOf '\\' < 0 then s
    else
        let sb = StringBuilder(s.Length)
        let mutable i = 0
        while i < s.Length do
            match s.[i] with
            | '\\' when i + 1 < s.Length ->
                sb.Append(match s.[i + 1] with 't' -> '\t' | 'n' -> '\n' | 'r' -> '\r' | c -> c) |> ignore
                i <- i + 2
            | c ->
                sb.Append c |> ignore
                i <- i + 1
        sb.ToString()

let encodeEntry (context: TypeRef) (body: W) : string =
    let w = Writer()
    writeType w context
    write w body
    escape (w.ToString())

let decodeEntry (payload: string) : TypeRef * W =
    let r = Reader(unescape payload)
    let context = readType r
    context, read r

/// What the map says about a block.
type Lookup =
    /// The type declaring the block's member, and the member's body.
    | Found of Type * Expr
    /// n members have a block on this line: refused, as from reflected definitions.
    | Shared of int
    | Missing

/// The member holding the block at `file:line`: the type declaring it and its body as an `Expr`,
/// generic parameters taken from the block's closure (or state machine) type. `lines` reads a
/// block's `body` section (`CaptureMap.body`). Missing when the map has no body for the block or
/// it does not decode here, whatever the reason: the block then needs the attribute, as without
/// a map.
let find (lines: string -> int -> string list option) (closureType: Type) (file: string) (line: int) : Lookup =
    try
        let payload =
            match lines file line with
            | Some [ x ] when x.StartsWith "X\t" -> Choice1Of2(x.Substring 2)
            | Some [ a ] when a.StartsWith "A\t" -> Choice2Of2(int (a.Substring 2))
            | Some [ y ] when y.StartsWith "Y\t" ->
                match y.Split '\t' with
                | [| _; f; l |] ->
                    match lines f (int l) with
                    | Some [ x ] when x.StartsWith "X\t" -> Choice1Of2(x.Substring 2)
                    | _ -> Choice2Of2 0
                | _ -> Choice2Of2 0
            | _ -> Choice2Of2 0
        match payload with
        | Choice1Of2 payload ->
            let context, body = decodeEntry payload
            let scope = Dictionary<string, Type>()
            if closureType.IsGenericType && not closureType.IsGenericTypeDefinition then
                Array.iter2 (fun (p: Type) a -> scope.[p.Name] <- a) (closureType.GetGenericTypeDefinition().GetGenericArguments()) (closureType.GetGenericArguments())
            Found(resolve scope context, decode scope body)
        | Choice2Of2 n when n > 1 -> Shared n
        | Choice2Of2 _ -> Missing
    with _ -> Missing
