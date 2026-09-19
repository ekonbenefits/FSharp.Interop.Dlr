namespace FSharp.Interop.Dlr

open System
open FSharp.Quotations
open FSharp.Quotations.Patterns
open FSharp.Quotations.ExprShape

/// A `dlrq { }` block: F# builds its quotation on every call, with the captured values inside
/// as `ValueWithName` nodes. One traversal order serves twice — at compile time each such node
/// becomes a read of a slot in the delegate's `obj[]` parameter, and per call the same walk
/// reads the values off into that array. The walk also records the block's shape (node types,
/// literals, members), which is what identifies a compiled delegate: a generic enclosing
/// function instantiates the quotation with concrete types, so one site can have several.
module internal Quoted =

    let private getArray =
        match <@ fun (a: obj[]) (i: int) -> a.[i] @> with
        | Lambda(_, Lambda(_, Call(None, mi, _))) -> mi
        | _ -> failwith "unreachable"

    /// The builder value itself is in the quotation too (as the receiver of its calls); it is
    /// not a capture.
    let private isCapture (builderType: Type) (t: Type) = t <> builderType

    /// The body of the `Delay` lambda: what F# passes is `builder.Delay(fun () -> body)`.
    let body (q: Expr) : Expr =
        match q with
        | Call(_, d, [ Lambda(_, body) ]) when d.Name = "Delay" -> body
        | other -> other

    /// Per call: the captured values in traversal order, and the shape.
    let scan (builderType: Type) (q: Expr) : obj[] * obj[] =
        let slots = ResizeArray<obj>()
        let shape = ResizeArray<obj>()
        let rec walk (e: Expr) =
            match e with
            | ValueWithName(v, t, _) when isCapture builderType t ->
                slots.Add v
                shape.Add t
            | Value(v, t) ->
                shape.Add v
                shape.Add t
            | ShapeVar v -> shape.Add v.Type
            | ShapeLambda(v, body) ->
                shape.Add v.Type
                walk body
            // `Expr.Type` throws on this node (FSharp.Core's typeOfConst has no LetRec case).
            | LetRecursive(bindings, body) ->
                for (v, def) in bindings do
                    shape.Add v.Type
                    walk def
                walk body
            | ShapeCombination(_, args) ->
                shape.Add(
                    match e with
                    | Call(_, mi, _) -> box mi
                    | NewObject(ci, _) -> box ci
                    | PropertyGet(_, pi, _) -> box pi
                    | _ -> box e.Type)
                List.iter walk args
        walk q
        slots.ToArray(), shape.ToArray()

    let sameShape (a: obj[]) (b: obj[]) =
        a.Length = b.Length && Array.forall2 (fun (x: obj) (y: obj) -> obj.Equals(x, y)) a b

    /// At compile time: the captures replaced by reads of `slots`, numbered in the order `scan`
    /// fills them.
    let prepare (builderType: Type) (slots: Var) (q: Expr) : Expr =
        let mutable next = 0
        let rec rewrite (e: Expr) =
            match e with
            | ValueWithName(_, t, _) when isCapture builderType t ->
                let read = Expr.Call(getArray, [ Expr.Var slots; Expr.Value next ])
                next <- next + 1
                if t = typeof<obj> then read else Expr.Coerce(read, t)
            | ShapeVar _ -> e
            | ShapeLambda(v, body) -> Expr.Lambda(v, rewrite body)
            | ShapeCombination(shape, args) -> RebuildShapeCombination(shape, List.map rewrite args)
        rewrite q
