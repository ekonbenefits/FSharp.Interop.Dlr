namespace FSharp.Interop.DLR

open System
open System.Collections.Generic
open System.Linq.Expressions
open FSharp.Quotations
open FSharp.Quotations.Patterns
open FSharp.Quotations.DerivedPatterns
open FSharp.Quotations.ExprShape
open FSharp.Reflection
open Microsoft.FSharp.Linq.RuntimeHelpers

/// Raised when a `dlr { }` body uses something the translator does not handle.
type DlrTranslationException(message: string) =
    inherit Exception(message)

/// Turns the quotation captured by `dlr { }` into a `Func<obj[], 'T>` whose DLR call
/// sites are constants, plus the per-call extraction of the closure values it reads.
module internal Translate =

    /// A compiled `dlr { }` site.
    type Compiled =
        { Delegate: Delegate
          SlotCount: int
          ResultType: Type }

    /// Every `Value` node in pre-order becomes a slot, in this order. Extraction (per call)
    /// and rewriting (once) both walk this way, so they agree without a shared table.
    /// Literal member names and the builder receiver get slots too; they are simply unused.
    let rec private walkValues (f: Expr -> unit) (e: Expr) =
        match e with
        | Value _ -> f e
        | ShapeVar _ -> ()
        | ShapeLambda(_, body) -> walkValues f body
        | ShapeCombination(_, args) -> for a in args do walkValues f a

    /// The closure values for one call, in slot order.
    let extractSlots (e: Expr) : obj[] =
        let acc = ResizeArray<obj>()
        walkValues (fun v -> match v with Value(o, _) -> acc.Add o | _ -> ()) e
        acc.ToArray()

    let private unsupported (what: string) (e: Expr) =
        raise (DlrTranslationException(sprintf "dlr { } does not support %s: %A" what e))

    let private genericDef (mi: Reflection.MethodInfo) =
        if mi.IsGenericMethod then mi.GetGenericMethodDefinition() else mi

    let private opMethod (e: Expr<_>) =
        match e with
        | Lambdas(_, Call(None, mi, _)) -> genericDef mi
        | _ -> failwith "operator definition expected"

    let private opDynamic = opMethod <@ fun (t: obj) (n: string) -> ((?) t n) : obj @>
    let private opDynamicAssign = opMethod <@ fun (t: obj) (n: string) (v: obj) -> (?<-) t n v @>
    let private opBang = opMethod <@ fun (t: obj) -> ((!?) t) : obj @>
    let private opGetIndex = opMethod <@ fun (t: obj) (i: obj) -> (getIndex t i) : obj @>
    let private opSetIndex = opMethod <@ fun (t: obj) (i: obj) (v: obj) -> setIndex t i v @>

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

    let private (|Op|_|) (def: Reflection.MethodInfo) (e: Expr) =
        match e with
        | Call(None, mi, args) when genericDef mi = def -> Some args
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

    /// `Named {| a = x; b = y |}`: the field names and values. F# evaluates the fields in
    /// source order through `let` temporaries and then builds the record in its own (sorted)
    /// field order, so the temporaries come back as bindings to wrap around the call.
    let private (|NamedRecord|_|) (e: Expr) =
        match e with
        | NewUnionCase(uc, [ arg ]) when uc.DeclaringType.IsGenericType && uc.DeclaringType.GetGenericTypeDefinition() = typedefof<Named<_>> ->
            let rec peel (bindings: (Var * Expr) list) (e: Expr) =
                match e with
                | Let(v, value, body) -> peel ((v, value) :: bindings) body
                | NewRecord(recordType, values) ->
                    let fields = FSharpType.GetRecordFields recordType
                    Some(List.rev bindings, List.zip [ for f in fields -> f.Name ] values)
                | other -> unsupported "Named applied to anything but an anonymous record literal" other
            peel [] arg
        | _ -> None

    /// Strips the boxing F# inserts on the way to an `obj` parameter, so the binder can see the
    /// real static type.
    let private (|Unboxed|) (e: Expr) =
        match e with
        | Coerce(inner, t) when t = typeof<obj> -> inner
        | _ -> e

    /// The compiler eta-expands a dynamic member used as a statement:
    /// `let clo = x?Foo in fun a -> clo a` applied to `()`. Fold it back to `x?Foo`.
    let rec private (|EtaReduced|) (e: Expr) =
        match e with
        | Let(v, value, Lambda(x, Application(Var v', Var x'))) when v = v' && x = x' -> value
        | _ -> e

    /// `x?Foo()` applies unit; `x?Foo(a, b)` applies a tuple; `x?Foo(a)` applies one value.
    let private splitArgs (e: Expr) =
        match e with
        | Value(_, t) when t = typeof<unit> -> []
        | NewTuple items -> items
        | single -> [ single ]

    let translate (builderType: Type) (resultType: Type) (quotation: Expr) : Compiled =
        let closure = Var("closure", typeof<obj[]>)
        let arrayGet = typeof<obj[]>.GetMethod("Get")

        // Slot index by node identity, assigned in the same order extractSlots reads them.
        let slots = Dictionary<Expr, int>(HashIdentity.Reference)
        walkValues (fun v -> slots.[v] <- slots.Count) quotation

        let slotRead (e: Expr) =
            let read = Expr.Call(Expr.Var closure, arrayGet, [ Expr.Value slots.[e] ])
            if e.Type = typeof<obj> then read else Expr.Coerce(read, e.Type)

        let isBuilder (receiver: Expr option) =
            match receiver with
            | Some r -> r.Type = builderType
            | None -> false

        let rec rewrite (e: Expr) : Expr =
            match e with
            // CE plumbing
            | Call(receiver, mi, args) when isBuilder receiver ->
                match mi.Name, args with
                | "Delay", [ Lambda(_, body) ] -> rewrite body
                | "Return", [ value ] -> rewrite value
                | "Zero", [] -> Expr.Value(())
                | name, _ -> unsupported (sprintf "the '%s' construct" name) e

            // Dynamic operations
            | Application(EtaReduced(Op opDynamic [ Unboxed target; Literal name ]), argExpr) ->
                let discard = e.Type = typeof<unit>
                let bindings, args = argList argExpr
                Binders.invokeMember (string name) discard (targetArg target) args |> finish discard e.Type |> bind bindings
            | Op opDynamic [ Unboxed target; Literal name ] ->
                if FSharpType.IsFunction e.Type then
                    unsupported "a dynamic member used as a first-class function; apply it directly" e
                Binders.getMember (string name) (targetArg target) |> Binders.convert e.Type
            | Op opDynamicAssign [ Unboxed target; Literal name; Unboxed value ] ->
                Binders.setMember (string name) (targetArg target) (valueArg value) |> Binders.convert typeof<unit>
            | Application(EtaReduced(Op opBang [ Unboxed target ]), argExpr) ->
                let discard = e.Type = typeof<unit>
                let bindings, args = argList argExpr
                Binders.invoke discard (targetArg target) args |> finish discard e.Type |> bind bindings
            | Op opGetIndex [ Unboxed target; Unboxed indexes ] ->
                Binders.getIndex (targetArg target) (indexList indexes) |> Binders.convert e.Type
            | Op opSetIndex [ Unboxed target; Unboxed indexes; Unboxed value ] ->
                Binders.setIndex (targetArg target) (indexList indexes) (valueArg value) |> Binders.convert typeof<unit>
            | BinaryOp(op, Unboxed left, Unboxed right) ->
                Binders.binaryOperation op (valueArg left) (valueArg right) |> Binders.convert e.Type

            // Everything else: closure values become slot reads, structure is rebuilt as-is.
            | Value(_, t) when t = typeof<unit> -> e
            | Value _ -> slotRead e
            | ShapeVar _ -> e
            | ShapeLambda(v, body) -> Expr.Lambda(v, rewrite body)
            | ShapeCombination(shape, args) -> RebuildShapeCombination(shape, List.map rewrite args)

        and targetArg (target: Expr) =
            let t = rewrite target
            Binders.dynamicArg (if t.Type = typeof<obj> then t else Expr.Coerce(t, typeof<obj>))

        and valueArg (value: Expr) = Binders.typedArg (rewrite value)

        and argList (argExpr: Expr) =
            let bindings = ResizeArray()
            let args =
                [ for a in splitArgs argExpr do
                    match a with
                    | NamedRecord(lets, fields) ->
                        bindings.AddRange lets
                        for (name, v) in fields -> Binders.named name (valueArg v)
                    | Unboxed v -> yield valueArg v ]
            List.ofSeq bindings, args

        and bind (bindings: (Var * Expr) list) (call: Expr) =
            List.foldBack (fun (v, value) body -> Expr.Let(v, rewrite value, body)) bindings call

        and indexList (indexes: Expr) =
            match indexes with
            | NewTuple items -> [ for Unboxed i in items -> valueArg i ]
            | single -> [ valueArg single ]

        and finish discard (resultType: Type) (call: Expr) =
            if discard then call else Binders.convert resultType call

        let body = rewrite quotation
        let delegateType = typedefof<Func<_, _>>.MakeGenericType(typeof<obj[]>, resultType)
        let lambda = Expr.NewDelegate(delegateType, [ closure ], body)
        let linq =
            try LeafExpressionConverter.QuotationToExpression lambda :?> LambdaExpression
            with :? DlrTranslationException -> reraise ()
               | ex -> raise (DlrTranslationException(sprintf "dlr { } could not compile this body: %s\n%A" ex.Message body))
        { Delegate = linq.Compile()
          SlotCount = slots.Count
          ResultType = resultType }
