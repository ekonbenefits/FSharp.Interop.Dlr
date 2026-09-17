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

/// Turns the reflected body of a `dlr { }` block into a `Func<obj, 'T>` over its Delay closure,
/// with the DLR call sites baked in as constants.
module internal Translate =

    /// A compiled `dlr { }` site: a `Func<obj, 'T>` over the Delay closure object.
    type Compiled =
        { Delegate: Delegate
          ResultType: Type }

    let private unsupported (what: string) (e: Expr) =
        raise (DlrTranslationException(sprintf "dlr { } does not support %s: %A" what e))

    let private genericDef (mi: Reflection.MethodInfo) =
        if mi.IsGenericMethod then mi.GetGenericMethodDefinition() else mi

    /// The generic definition of the method a marker quotation calls. Curried static members
    /// quote as applications of an inner lambda, so this looks for the first call anywhere.
    let private opMethod (e: Expr<_>) =
        let rec find (e: Expr) =
            match e with
            | Call(None, mi, _) -> Some(genericDef mi)
            | ShapeVar _ -> None
            | ShapeLambda(_, body) -> find body
            | ShapeCombination(_, args) -> args |> List.tryPick find
        match find e with
        | Some mi -> mi
        | None -> failwith "operator definition expected"

    let private opDynamic = opMethod <@ fun (t: obj) (n: string) -> ((?) t n) : obj @>
    let private opDynamicAssign = opMethod <@ fun (t: obj) (n: string) (v: obj) -> (?<-) t n v @>
    let private opCall = opMethod <@ fun (a: obj) (t: obj) -> (Dlr.call a t) : obj @>
    let private opNamed = opMethod <@ fun (r: obj) -> Dlr.named r @>
    /// `Dlr.new'<T>(a, b, …)`: the type and the arguments (each unboxed to its static type).
    let private (|New|_|) (e: Expr) =
        match e with
        | Call(None, mi, args) when mi.DeclaringType = typeof<Dlr> && mi.Name = "new'" ->
            Some(mi.GetGenericArguments().[0], [ for a in args -> match a with Coerce(inner, t) when t = typeof<obj> -> inner | a -> a ])
        | _ -> None
    let private opCast = opMethod <@ fun (v: obj) -> Dlr.cast<obj> v @>
    let private opImplicit = opMethod <@ fun (v: obj) -> (Dlr.implicit v) : obj @>
    let private opGet = opMethod <@ fun (n: string) (t: obj) -> (Dlr.get n t) : obj @>
    let private opSet = opMethod <@ fun (n: string) (v: obj) (t: obj) -> Dlr.set n v t @>
    let private opAddAssign = opMethod <@ fun (n: string) (v: obj) (t: obj) -> Dlr.addAssign n v t @>
    let private opSubtractAssign = opMethod <@ fun (n: string) (v: obj) (t: obj) -> Dlr.subtractAssign n v t @>
    let private opInvoke = opMethod <@ fun (n: string) (a: obj) (t: obj) -> (Dlr.invoke n a t) : obj @>
    let private unaryOps =
        dict [
            opMethod <@ fun (v: obj) -> (Dlr.neg v) : obj @>, ExpressionType.Negate
            opMethod <@ fun (v: obj) -> (Dlr.not v) : obj @>, ExpressionType.Not
            opMethod <@ fun (v: obj) -> (Dlr.complement v) : obj @>, ExpressionType.OnesComplement
        ]
    let private forEach = opMethod <@ fun (items: seq<obj>) (body: Func<obj, unit>) -> DlrRuntime.forEach items body @>
    let private whileLoop = opMethod <@ fun (guard: Func<bool>) (body: Func<unit>) -> DlrRuntime.whileLoop guard body @>
    let private tryWith = opMethod <@ fun (body: Func<obj>) (handler: Func<exn, obj>) -> DlrRuntime.tryWith body handler @>
    let private tryFinally = opMethod <@ fun (body: Func<obj>) (fin: Func<unit>) -> DlrRuntime.tryFinally body fin @>
    let private using = opMethod <@ fun (r: IDisposable) (body: Func<IDisposable, obj>) -> DlrRuntime.using r body @>
    let private opItem = opMethod <@ fun (i: obj) (t: obj) -> (Dlr.item i t) : obj @>
    let private opSetItem = opMethod <@ fun (i: obj) (v: obj) (t: obj) -> Dlr.setItem i v t @>

    let private binaryOps =
        dict [
            opMethod <@ fun (l: obj) (r: obj) -> (l ?%? r) : obj @>, ExpressionType.Modulo
            opMethod <@ fun (l: obj) (r: obj) -> (l ?*? r) : obj @>, ExpressionType.Multiply
            opMethod <@ fun (l: obj) (r: obj) -> (l ?+? r) : obj @>, ExpressionType.Add
            opMethod <@ fun (l: obj) (r: obj) -> (l ?-? r) : obj @>, ExpressionType.Subtract
            opMethod <@ fun (l: obj) (r: obj) -> (l ?/? r) : obj @>, ExpressionType.Divide
            opMethod <@ fun (l: obj) (r: obj) -> (l ?&&&? r) : obj @>, ExpressionType.And
            opMethod <@ fun (l: obj) (r: obj) -> (l ?|||? r) : obj @>, ExpressionType.Or
            opMethod <@ fun (l: obj) (r: obj) -> (l ?^^^? r) : obj @>, ExpressionType.ExclusiveOr
            opMethod <@ fun (l: obj) (r: obj) -> (l ?<<<? r) : obj @>, ExpressionType.LeftShift
            opMethod <@ fun (l: obj) (r: obj) -> (l ?>>>? r) : obj @>, ExpressionType.RightShift
            opMethod <@ fun (l: obj) (r: obj) -> l ?<=? r @>, ExpressionType.LessThanOrEqual
            opMethod <@ fun (l: obj) (r: obj) -> l ?<>? r @>, ExpressionType.NotEqual
            opMethod <@ fun (l: obj) (r: obj) -> l ?<? r @>, ExpressionType.LessThan
            opMethod <@ fun (l: obj) (r: obj) -> l ?=? r @>, ExpressionType.Equal
            opMethod <@ fun (l: obj) (r: obj) -> l ?>? r @>, ExpressionType.GreaterThan
            opMethod <@ fun (l: obj) (r: obj) -> l ?>=? r @>, ExpressionType.GreaterThanOrEqual
        ]

    /// A literal `[ typeof<A>; typeof<B>; … ]`.
    let rec private (|TypeOfList|_|) (e: Expr) =
        match e with
        | NewUnionCase(empty, []) when empty.Name = "Empty" -> Some []
        | NewUnionCase(cons, [ Call(None, mi, []); TypeOfList rest ]) when cons.Name = "Cons" && mi.Name = "TypeOf" && mi.IsGenericMethod ->
            Some(mi.GetGenericArguments().[0] :: rest)
        | _ -> None

    /// `Dlr.typeArgs<A, B>()` or `Dlr.typeArgsOf [ typeof<A>; typeof<B> ]`: the explicit type
    /// arguments of the call being built. The list form must be a literal of `typeof`s: the site
    /// is created with its type arguments, so a list only known at run time has no site to use
    /// (the computed-name cache is the shape that would need; not yet).
    let private (|TypeArgs|_|) (e: Expr) =
        match e with
        | Call(None, mi, []) when mi.DeclaringType = typeof<Dlr> && mi.Name = "typeArgs" -> Some(List.ofArray (mi.GetGenericArguments()))
        | Call(None, mi, [ TypeOfList ts ]) when mi.DeclaringType = typeof<Dlr> && mi.Name = "typeArgsOf" -> Some ts
        | Call(None, mi, [ other ]) when mi.DeclaringType = typeof<Dlr> && mi.Name = "typeArgsOf" ->
            raise (DlrTranslationException(sprintf "dlr { } needs Dlr.typeArgsOf's list to be a literal of typeof<…> (a list known only at run time is not supported yet): %A" other))
        | _ -> None

    let private (|Op|_|) (def: Reflection.MethodInfo) (e: Expr) =
        match e with
        | Call(None, mi, args) when genericDef mi = def -> Some args
        | _ -> None

    let private (|UnaryOp|_|) (e: Expr) =
        match e with
        | Call(None, mi, [ v ]) ->
            match unaryOps.TryGetValue(genericDef mi) with
            | true, op -> Some(op, v)
            | _ -> None
        | _ -> None

    let private (|BinaryOp|_|) (e: Expr) =
        match e with
        | Call(None, mi, [ l; r ]) ->
            match binaryOps.TryGetValue(genericDef mi) with
            | true, op -> Some(op, l, r)
            | _ -> None
        | _ -> None

    let private (|Literal|_|) (e: Expr) =
        match e with
        | Value(o, _) -> Some o
        | _ -> None

    let private isLiteral (e: Expr) = match e with Value _ -> true | _ -> false

    /// Strips the boxing F# inserts on the way to an `obj` parameter, so the binder can see the
    /// real static type.
    let private (|Unboxed|) (e: Expr) =
        match e with
        | Coerce(inner, t) when t = typeof<obj> -> inner
        | _ -> e

    /// `Dlr.named {| a = x; b = y |}`: the field names and values. F# evaluates the fields in
    /// source order through `let` temporaries and then builds the record in its own (sorted)
    /// field order, so the temporaries come back as bindings to wrap around the call.
    let private (|NamedRecord|_|) (e: Expr) =
        let rec peel (bindings: (Var * Expr) list) (e: Expr) =
            match e with
            | Let(v, value, body) -> peel ((v, value) :: bindings) body
            | Op opNamed [ inner ] -> peel bindings inner
            | NewRecord(recordType, values) ->
                let fields = FSharpType.GetRecordFields recordType
                Some(List.rev bindings, List.zip [ for f in fields -> f.Name ] values)
            | other -> unsupported "Dlr.named applied to anything but an anonymous record literal" other
        match e with
        | Op opNamed _ -> peel [] e
        | _ -> None

    /// The compiler eta-expands a dynamic member used as a statement:
    /// `let clo = x?Foo in fun a -> clo a` applied to `()`. Fold it back to `x?Foo`.
    let rec private (|EtaReduced|) (e: Expr) =
        match e with
        | Let(v, value, Lambda(x, Application(Var v', Var x'))) when v = v' && x = x' -> value
        | _ -> e

    /// One of the member operations, whichever way it was spelled: `x?Name` / `(?) x name` /
    /// `Dlr.get name x`, those applied to arguments (`x?Name(a)`, `(Dlr.get name x)(a)`) or
    /// `Dlr.invoke name a x`, and `x?Name <- v` / `Dlr.set name v x`. Target, name
    /// expression, and the argument expression (for an invocation) or value (for a set).
    type private MemberOp =
        | GetMember of target: Expr * name: Expr
        | InvokeMember of target: Expr * name: Expr * args: Expr
        | SetMember of target: Expr * name: Expr * value: Expr

    let private (|MemberOp|_|) (e: Expr) =
        match e with
        | Application(EtaReduced(Op opDynamic [ Unboxed target; name ]), args) -> Some(InvokeMember(target, name, args))
        | Op opDynamic [ Unboxed target; name ] -> Some(GetMember(target, name))
        | Op opDynamicAssign [ Unboxed target; name; Unboxed value ] -> Some(SetMember(target, name, value))
        | Application(EtaReduced(Op opGet [ name; Unboxed target ]), args) -> Some(InvokeMember(target, name, args))
        | Op opGet [ name; Unboxed target ] -> Some(GetMember(target, name))
        | Op opInvoke [ name; args; Unboxed target ] -> Some(InvokeMember(target, name, args))
        | Op opSet [ name; Unboxed value; Unboxed target ] -> Some(SetMember(target, name, value))
        | _ -> None

    /// `x?Foo()` applies unit; `x?Foo(a, b)` applies a tuple; `x?Foo(a)` applies one value.
    let private splitArgs (e: Expr) =
        match e with
        | Value(_, t) when t = typeof<unit> -> []
        | NewTuple items -> items
        | single -> [ single ]

    /// Compiles the reflected body of one `dlr { }` block. `closureType` is the compiler-generated
    /// class of the `Delay` closure: its fields, named after the captured variables, are where
    /// the body's free variables are read from at call time.
    /// The definition of a let-bound variable somewhere in `e` (quotation Vars are identity-based,
    /// so shadowing is not a concern).
    let rec private letDefinition (v: Var) (e: Expr) : Expr option =
        match e with
        | Let(v', def, _) when v' = v -> Some def
        | ShapeVar _ -> None
        | ShapeLambda(_, body) -> letDefinition v body
        | ShapeCombination(_, args) -> args |> List.tryPick (letDefinition v)

    /// The argument a parameter of a let-bound local function takes, when the member body
    /// applies that function exactly once: the optimizer inlines such a function at its call
    /// site, so its parameters become that call's arguments and are never captured.
    let private parameterArgument (v: Var) (memberBody: Expr) : Expr option =
        let rec lambdaParams (e: Expr) =
            match e with
            | Lambda(p, body) -> let ps, inner = lambdaParams body in p :: ps, inner
            | _ -> [], e
        let rec applications (f: Var) (e: Expr) : Expr list list =
            // Full application chains f a1 a2 … of `f`, innermost first.
            let rec chain (e: Expr) (acc: Expr list) =
                match e with
                | Application(inner, arg) -> chain inner (arg :: acc)
                | Var f' when f' = f -> Some acc
                | _ -> None
            match chain e [] with
            | Some args when not args.IsEmpty -> [ args ]
            | _ ->
                match e with
                | ShapeVar _ -> []
                | ShapeLambda(_, body) -> applications f body
                | ShapeCombination(_, args) -> args |> List.collect (applications f)
        let rec search (e: Expr) : Expr option =
            match e with
            | Let(f, def, rest) when (let ps, _ = lambdaParams def in List.contains v ps) ->
                let ps, _ = lambdaParams def
                let index = List.findIndex ((=) v) ps
                match applications f rest with
                | [ args ] when args.Length > index -> Some args.[index]
                | [] -> None
                | _ -> None
            | ShapeVar _ -> None
            | ShapeLambda(_, body) -> search body
            | ShapeCombination(_, args) -> args |> List.tryPick search
        search memberBody

    // `|>` is inlined inside a quotation literal, so these come from reflection instead.
    let private fsharpOperators = typeof<obj list>.Assembly.GetType("Microsoft.FSharp.Core.Operators")
    let private pipeRight = fsharpOperators.GetMethod("op_PipeRight")
    let private pipeLeft = fsharpOperators.GetMethod("op_PipeLeft")

    /// `x |> f` in a reflected body is `op_PipeRight(x, let name = "A" in fun target -> …)`, and a
    /// curried marker applied to its arguments is `Application(Lambda(name, Lambda(target, …)), …)`.
    /// Apply such functions to their arguments (a parameter used at most once is substituted;
    /// otherwise it is let-bound, so nothing is evaluated twice) and inline `let`s of literals and
    /// variables, so `w |> Dlr.get "A"` becomes the plain `Dlr.get "A" w` call the member-op
    /// patterns recognise, with the name a literal and a tuple argument still a tuple.
    let rec private normalize (e: Expr) : Expr =
        let rec occurrences (v: Var) (e: Expr) =
            match e with
            | Var v' when v' = v -> 1
            | ShapeVar _ -> 0
            | ShapeLambda(_, body) -> occurrences v body
            | ShapeCombination(_, args) -> args |> List.sumBy (occurrences v)
        let rec apply (f: Expr) (x: Expr) : Expr =
            match f with
            | Let(v, value, body) -> Expr.Let(v, value, apply body x)
            | Lambda(p, body) ->
                if occurrences p body <= 1 then body.Substitute(fun v -> if v = p then Some x else None)
                else Expr.Let(p, x, body)
            | _ -> Expr.Application(f, x)
        let applied (f: Expr) (x: Expr) =
            match normalize f, normalize x with
            | (Lambda _ | Let _ as f), x -> normalize (apply f x)
            | f, x -> Expr.Application(f, x)
        match e with
        | Op pipeRight [ x; f ] -> applied f x
        | Op pipeLeft [ f; x ] -> applied f x
        | Application(f, x) -> applied f x
        | Let(v, (Value _ | Var _ as value), body) -> normalize (body.Substitute(fun v' -> if v' = v then Some value else None))
        // The eta-expanded statement form `let clo = x?Foo in clo ()` once its lambda is applied.
        | Let(v, value, Application(Var v', arg)) when v = v' -> applied value arg
        | ShapeVar _ -> e
        | ShapeLambda(v, body) -> Expr.Lambda(v, normalize body)
        | ShapeCombination(shape, args) -> RebuildShapeCombination(shape, List.map normalize args)

    let translate (builderType: Type) (context: Type) (memberBody: Expr) (closureType: Type) (resultType: Type) (body: Expr) : Compiled =
        let convert = Binders.convert context
        let closure = Var("closure", typeof<obj>)
        let self = Expr.Coerce(Expr.Var closure, closureType)
        let fields =
            closureType.GetFields(Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic)
            |> Array.map (fun f -> f.Name, f)
            |> dict

        /// A free variable of the body becomes a read of the closure field of the same name.
        /// A captured `let mutable` is stored as an FSharpRef cell; read through it. When there is
        /// no such field the optimizer inlined the variable's definition (a literal, a local
        /// function, ...) instead of capturing it, so substitute that definition from the
        /// enclosing member's body; its own free variables resolve the same way.
        let captured (resolve: Expr -> Expr) (v: Var) : Expr =
            match fields.TryGetValue v.Name with
            | true, f when f.FieldType = v.Type -> Expr.FieldGet(self, f)
            | true, f when f.FieldType.IsGenericType
                           && f.FieldType.GetGenericTypeDefinition() = typedefof<Ref<_>>
                           && f.FieldType.GetGenericArguments().[0] = v.Type ->
                Expr.PropertyGet(Expr.FieldGet(self, f), f.FieldType.GetProperty("Value"))
            | true, f ->
                raise (DlrTranslationException(
                        sprintf "dlr { } captured '%s' as %s but the body uses it as %s." v.Name f.FieldType.Name v.Type.Name))
            | _ ->
                match letDefinition v memberBody with
                | Some def -> resolve def
                | None ->
                    match parameterArgument v memberBody with
                    | Some arg -> resolve arg
                    | None ->
                        raise (DlrTranslationException(
                                sprintf "dlr { } could not find captured variable '%s' on closure %s (fields: %s), a let binding for it, or a single application supplying it (the optimizer inlined it; give the enclosing local function more than one call site or hoist the block)."
                                    v.Name closureType.Name (String.Join(", ", fields.Keys))))

        /// A `unit` expression may compile to a void call, which cannot be the value of a lambda or
        /// of the block; end it with the unit constant so the tree has a `Unit` value.
        let asUnit (e: Expr) =
            if e.Type = typeof<unit> then Expr.Sequential(e, Expr.Value(())) else e

        /// Variables bound inside the expression being rewritten are left alone; anything else
        /// that is not the builder comes from the closure or the enclosing member.
        let isCaptured (bound: Set<Var>) (v: Var) = not (bound.Contains v) && v.Type <> builderType

        let isBuilder (receiver: Expr option) =
            match receiver with
            | Some r -> r.Type = builderType
            | None -> false

        /// A body as a `Func<..>` delegate over `vars` (see DlrRuntime for why not an F# function).
        /// The delegate's type is built from the variables' types and the body's; a `unit` body
        /// (which may compile to a void call) becomes a `Func<.., unit>`, not an `Action`.
        let rec func (bound: Set<Var>) (vars: Var list) (body: Expr) : Expr =
            let bound = vars |> List.fold (fun b v -> Set.add v b) bound
            let body = asUnit (rewriteIn bound body)
            let delegateType = Expression.GetFuncType(Array.append (vars |> List.map (fun v -> v.Type) |> Array.ofList) [| body.Type |])
            Expr.NewDelegate(delegateType, vars, body)

        and rewriteIn (bound: Set<Var>) (e: Expr) : Expr =
            let rewrite = rewriteIn bound
            match e with
            // CE plumbing (Discover already unwrapped the outermost Delay)
            | Call(receiver, mi, args) when isBuilder receiver ->
                match mi.Name, args with
                | "Return", [ value ] -> rewrite value
                | "Zero", [] -> Expr.Value(())
                | "Combine", [ first; Call(_, d, [ Lambda(_, rest) ]) ] when d.Name = "Delay" ->
                    Expr.Sequential(rewrite first, rewrite rest)
                | "For", [ items; Lambda(x, body) ] ->
                    let items = rewrite items
                    Expr.Call(forEach.MakeGenericMethod(x.Type), [ items; func bound [ x ] body ])
                | "While", [ Lambda(_, guard); Call(_, d, [ Lambda(_, body) ]) ] when d.Name = "Delay" ->
                    Expr.Call(whileLoop, [ func bound [] guard; func bound [] body ])
                | "TryWith", [ Call(_, d, [ Lambda(_, body) ]); Lambda(ex, handler) ] when d.Name = "Delay" ->
                    Expr.Call(tryWith.MakeGenericMethod(e.Type), [ func bound [] body; func bound [ ex ] handler ])
                | "TryFinally", [ Call(_, d, [ Lambda(_, body) ]); Lambda(_, compensation) ] when d.Name = "Delay" ->
                    Expr.Call(tryFinally.MakeGenericMethod(e.Type), [ func bound [] body; func bound [] compensation ])
                | "Using", [ resource; Lambda(r, body) ] ->
                    Expr.Call(using.MakeGenericMethod(r.Type, e.Type), [ rewrite resource; func bound [ r ] body ])
                // A nested dlr { } is compiled as part of this one: at run time its closure would be
                // made by our compiled code, not the F# compiler, so it has no reflected body of its own.
                | "Run", [ Call(_, d, [ Lambda(_, inner) ]); _; _ ] when d.Name = "Delay" -> rewrite inner
                | name, _ -> unsupported (sprintf "the '%s' construct" name) e

            // Member operations: a literal name is a baked site; a computed name binds per name.
            | MemberOp(InvokeMember(target, nameExpr, argExpr)) ->
                let typeArgs, argExprs =
                    match splitArgs argExpr with
                    | TypeArgs ts :: rest -> ts, rest
                    | args -> [], args
                match nameExpr with
                | Literal name ->
                    let discard = e.Type = typeof<unit>
                    let bindings, args = argList bound argExprs
                    Binders.invokeMemberOrApply context (string name) typeArgs discard (targetArg bound target) args |> finish discard e.Type |> bind bound bindings
                | _ ->
                    computedName bound nameExpr target argExprs e.Type (fun name targetArg args ->
                        Binders.invokeMemberOrApply context name typeArgs false targetArg args)
            | MemberOp(GetMember(target, nameExpr)) when FSharpType.IsFunction e.Type ->
                // Read as an F# function: a curried invoker of the member (method, delegate or F#
                // function), so `let f: int -> int -> int = dlr { return x?Add }` then `f 1 2`.
                match nameExpr with
                | Literal name -> Binders.functionMember context (string name) e.Type (targetArg bound target)
                | _ -> computedName bound nameExpr target [] e.Type (fun name targetArg _ -> Binders.functionMember context name e.Type targetArg)
            | MemberOp(GetMember(target, nameExpr)) ->
                match nameExpr with
                | Literal name -> Binders.getMember context (string name) (targetArg bound target) |> convert e.Type
                | _ -> computedName bound nameExpr target [] e.Type (fun name targetArg _ -> Binders.getMember context name targetArg)
            | Op opAddAssign [ nameExpr; Unboxed value; Unboxed target ] -> compoundAssign bound false nameExpr target value
            | Op opSubtractAssign [ nameExpr; Unboxed value; Unboxed target ] -> compoundAssign bound true nameExpr target value
            | MemberOp(SetMember(target, nameExpr, value)) ->
                match nameExpr with
                | Literal name -> Binders.setMember context (string name) (targetArg bound target) (valueArg bound value) |> convert typeof<unit>
                | _ ->
                    computedName bound nameExpr target [ value ] typeof<unit> (fun name targetArg args ->
                        Binders.setMember context name targetArg (List.head args))
            | New(t, argExprs) ->
                let bindings, args = argList bound argExprs
                Binders.invokeConstructor context t args |> convert e.Type |> bind bound bindings
            | Op opCall [ argExpr; Unboxed target ] ->
                let discard = e.Type = typeof<unit>
                let bindings, args = argList bound (splitArgs argExpr)
                Binders.invokeOrApply context discard (targetArg bound target) args |> finish discard e.Type |> bind bound bindings
            | Op opItem [ Unboxed indexes; Unboxed target ] ->
                Binders.getIndex context (targetArg bound target) (indexList bound (splitArgs indexes)) |> convert e.Type
            | Op opSetItem [ Unboxed indexes; Unboxed value; Unboxed target ] ->
                Binders.setIndex context (targetArg bound target) (indexList bound (splitArgs indexes)) (valueArg bound value) |> convert typeof<unit>
            | BinaryOp(op, Unboxed left, Unboxed right) ->
                Binders.binaryOperation context op (valueArg bound left) (valueArg bound right) |> convert e.Type
            | UnaryOp(op, Unboxed operand) ->
                Binders.unaryOperation context op (valueArg bound operand) |> convert e.Type
            | Op opCast [ Unboxed value ] ->
                let v = rewrite value
                Binders.convertExplicit context e.Type (if v.Type = typeof<obj> then v else Expr.Coerce(v, typeof<obj>))
            | Op opImplicit [ Unboxed value ] ->
                let v = rewrite value
                convert e.Type (if v.Type = typeof<obj> then v else Expr.Coerce(v, typeof<obj>))

            // Everything else: captured variables become field reads, structure is rebuilt as-is.
            | Var v when isCaptured bound v -> captured (rewriteIn bound) v
            | ShapeVar _ -> e
            | Let(v, def, letBody) -> Expr.Let(v, rewrite def, rewriteIn (bound.Add v) letBody)
            // `let rec` has no expression-tree form; tie the knot through reference cells, as the
            // compiler does: each binding becomes a cell, uses read the cell, and the definitions
            // are assigned after all cells exist so mutual recursion works too.
            | LetRecursive(bindings, letBody) ->
                let cells = [ for (v, _) in bindings -> v, Var(v.Name + "'", typedefof<Ref<_>>.MakeGenericType v.Type) ]
                let readCell (cell: Var) = Expr.PropertyGet(Expr.Var cell, cell.Type.GetProperty("Value"))
                let viaCells (e: Expr) = e.Substitute(fun v -> cells |> List.tryFind (fun (rv, _) -> rv = v) |> Option.map (snd >> readCell))
                let bound = cells |> List.fold (fun (b: Set<Var>) (_, c) -> b.Add c) bound
                let assignments =
                    [ for (_, def), (_, cell) in List.zip bindings cells ->
                        Expr.PropertySet(Expr.Var cell, cell.Type.GetProperty("Value"), rewriteIn bound (viaCells def)) ]
                let body = rewriteIn bound (viaCells letBody)
                let inner = List.foldBack (fun assign rest -> Expr.Sequential(assign, rest)) assignments body
                List.foldBack
                    (fun (v: Var, cell: Var) rest ->
                        Expr.Let(cell, Expr.NewObject(cell.Type.GetConstructor [| v.Type |], [ Expr.Value(null, v.Type) ]), rest))
                    cells inner
            | ShapeLambda(v, lambdaBody) -> Expr.Lambda(v, asUnit (rewriteIn (bound.Add v) lambdaBody))
            | ShapeCombination(shape, args) -> RebuildShapeCombination(shape, List.map rewrite args)

        /// `(?) x name` with a computed name: the operation's delegate is compiled once here with
        /// its call sites as parameters (lifted from a template built for a placeholder name), and
        /// a SiteCache constant creates the sites per distinct name; the emitted code is
        /// `let sites = cache.Get(name) in delegate.Invoke(sites.[0], …, target, args…)`.
        /// Argument names in `Dlr.named` stay static.
        and computedName bound (nameExpr: Expr) (target: Expr) (argExprs: Expr list) (resultType: Type) (site: string -> Binders.Arg -> Binders.Arg list -> Expr) : Expr =
            let rewrite = rewriteIn bound
            let bindings, argInfos = argList bound argExprs
            let targetVar = Var("target", typeof<obj>)
            let argVars = argInfos |> List.mapi (fun i a -> Var(sprintf "a%d" i, a.Type))
            let template (name: string) =
                let args = List.map2 (fun (info: Binders.Arg) (v: Var) -> { info with Expr = Expr.Var v }) argInfos argVars
                site name (Binders.dynamicArg (Expr.Var targetVar)) args
            // The operation's shape does not depend on the name, only its sites do: build it once
            // for a placeholder, lift every site constant into a parameter, and compile that one
            // delegate now. Per name, the cache creates the sites and hands them back in the
            // same order.
            let placeholder = template "name"
            let sites = SiteCache<string>.Sites placeholder
            let siteVars = sites |> List.mapi (fun i s -> s, Var(sprintf "site%d" i, s.GetType()))
            let rec lift (e: Expr) =
                match e with
                | Value(v, _) when (v :? CallSite) ->
                    match siteVars |> List.tryFind (fun (s, _) -> obj.ReferenceEquals(s, v)) with
                    | Some(_, var) -> Expr.Var var
                    | None -> e
                | ShapeVar _ -> e
                | ShapeLambda(v, body) -> Expr.Lambda(v, lift body)
                | ShapeCombination(shape, args) -> RebuildShapeCombination(shape, List.map lift args)
            let body = lift placeholder
            let delegateType =
                Expression.GetDelegateType(Array.ofList ([ for _, v in siteVars -> v.Type ] @ typeof<obj> :: [ for v in argVars -> v.Type ] @ [ typeof<obj> ]))
            let lambda =
                Expr.NewDelegate(delegateType, [ for _, v in siteVars -> v ] @ targetVar :: argVars, (if body.Type = typeof<obj> then body else Expr.Coerce(body, typeof<obj>)))
            let compiled = (Microsoft.FSharp.Linq.RuntimeHelpers.LeafExpressionConverter.QuotationToExpression lambda :?> LambdaExpression).Compile()
            let cache = SiteCache<string>(template)
            let sitesVar = Var("sites", typeof<CallSite[]>)
            let at = typeof<SiteCache<string>>.GetMethod("At")
            let siteArgs = siteVars |> List.mapi (fun i (_, v) -> Expr.Coerce(Expr.Call(at, [ Expr.Var sitesVar; Expr.Value i ]), v.Type))
            let t = rewrite target
            let targetExpr = if t.Type = typeof<obj> then t else Expr.Coerce(t, typeof<obj>)
            let call =
                Expr.Let(sitesVar, Expr.Call(Expr.Value(cache, typeof<SiteCache<string>>), typeof<SiteCache<string>>.GetMethod("Get"), [ rewrite nameExpr ]),
                         Expr.Call(Expr.Value(compiled, delegateType), delegateType.GetMethod("Invoke"), siteArgs @ targetExpr :: [ for a in argInfos -> a.Expr ]))
            (if FSharpType.IsFunction resultType then Expr.Coerce(call, resultType) else convert resultType call)
            |> bind bound bindings

        /// `Dlr.addAssign`/`subtractAssign`: bind the target and value once, then both branches
        /// of the IsEvent decision refer to them. A computed name goes through the SiteCache
        /// like any other member operation.
        and compoundAssign bound (subtract: bool) (nameExpr: Expr) (target: Expr) (value: Expr) : Expr =
            match nameExpr with
            | Literal name ->
                let t = rewriteIn bound target
                let v = rewriteIn bound value
                let tv = Var("target", typeof<obj>)
                let vv = Var("value", v.Type)
                // A literal value keeps C#'s constant conversions (a byte member += 1) even though
                // it is read through a variable here.
                let valueArg =
                    let a = Binders.typedArg (Expr.Var vv)
                    match value with
                    | Value _ -> Binders.constant a
                    | _ -> a
                let body = Binders.compoundAssign context (string name) subtract (Binders.dynamicArg (Expr.Var tv)) valueArg
                Expr.Let(tv, (if t.Type = typeof<obj> then t else Expr.Coerce(t, typeof<obj>)), Expr.Let(vv, v, body))
            | _ ->
                computedName bound nameExpr target [ value ] typeof<unit> (fun name targetArg args ->
                    // The name-cache template already makes target and value delegate parameters.
                    Expr.Sequential(Binders.compoundAssign context name subtract targetArg (List.head args), Expr.Value(null, typeof<obj>)))

        and targetArg bound (target: Expr) =
            let t = rewriteIn bound target
            Binders.dynamicArg (if t.Type = typeof<obj> then t else Expr.Coerce(t, typeof<obj>))

        and valueArg bound (value: Expr) =
            match value with
            | Value _ -> Binders.constant (Binders.typedArg value)
            | _ -> Binders.typedArg (rewriteIn bound value)

        and argList bound (argExprs: Expr list) =
            let bindings = ResizeArray()
            let args =
                [ for a in argExprs do
                    match a with
                    | TypeArgs _ -> unsupported "Dlr.typeArgs anywhere but as the first argument" a
                    | NamedRecord(lets, fields) ->
                        bindings.AddRange lets
                        let inner = bindings |> Seq.fold (fun (b: Set<Var>) (v, _) -> b.Add v) bound
                        for (name, v) in fields -> Binders.named name (valueArg inner v)
                    | Unboxed v -> yield valueArg bound v ]
            List.ofSeq bindings, args

        and bind bound (bindings: (Var * Expr) list) (call: Expr) =
            List.foldBack (fun (v, value) body -> Expr.Let(v, rewriteIn bound value, body)) bindings call

        and indexList bound (indexes: Expr list) = [ for Unboxed i in indexes -> valueArg bound i ]

        and finish discard (resultType: Type) (call: Expr) =
            if discard then call else convert resultType call

        let rewrite = rewriteIn Set.empty

        let delegateType = typedefof<Func<_, _>>.MakeGenericType(typeof<obj>, resultType)
        let linq =
            try
                let rewritten = asUnit (rewrite (normalize body))
                let lambda = Expr.NewDelegate(delegateType, [ closure ], rewritten)
                LeafExpressionConverter.QuotationToExpression lambda :?> LambdaExpression
            with :? DlrTranslationException -> reraise ()
               | ex -> raise (DlrTranslationException(sprintf "dlr { } could not compile this body: %s\n%A" ex.Message body))
        { Delegate = linq.Compile()
          ResultType = resultType }
