namespace FSharp.Interop.Dlr

open System
open System.Reflection
open FSharp.Quotations
open FSharp.Quotations.Patterns
open FSharp.Quotations.ExprShape

/// Finds the quoted body of a `dlr { }` block from its Delay closure, using the
/// `[<ReflectedDefinition>]` of the enclosing member.
module internal Discover =

    let private all =
        BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly

    /// Members with a reflected definition on `t` and every type nested in it (closures of a
    /// class member are nested in the enclosing module, not the class).
    let rec private reflected (t: Type) : seq<MethodBase * Expr> =
        seq {
            for m in t.GetMethods all do
                match Expr.TryGetReflectedDefinition m with
                | Some q -> yield (m :> MethodBase), q
                | None -> ()
            for c in t.GetConstructors all do
                match Expr.TryGetReflectedDefinition c with
                | Some q -> yield (c :> MethodBase), q
                | None -> ()
            for n in t.GetNestedTypes all do
                yield! reflected n
        }

    /// Every `builder.Run(builder.Delay(fun () -> body), file, line)` in `e` for this line.
    let rec private runsOnLine (builderType: Type) (line: int) (e: Expr) : Expr list =
        match e with
        | Call(Some receiver, mi, [ Call(_, delay, [ Lambda(_, body) ]); Value _; Value(l, _) ])
            when receiver.Type = builderType && mi.Name = "Run" && delay.Name = "Delay" && unbox<int> l = line ->
            body :: runsOnLine builderType line body
        | ShapeVar _ -> []
        | ShapeLambda(_, body) -> runsOnLine builderType line body
        | ShapeCombination(_, args) -> args |> List.collect (runsOnLine builderType line)

    /// The body for the block whose Delay closure has type `closureType`, at `file:line`.
    let findBody (builderType: Type) (closureType: Type) (file: string) (line: int) : Expr =
        let holder = if isNull closureType.DeclaringType then closureType else closureType.DeclaringType
        let matches =
            reflected holder
            |> Seq.collect (fun (m, q) -> runsOnLine builderType line q |> List.map (fun b -> m, b))
            |> List.ofSeq
        match matches with
        | [ _, body ] -> body
        | [] ->
            raise (DlrTranslationException(
                    sprintf "dlr { } at %s:%d needs [<ReflectedDefinition>] on its enclosing module, type or member so the body can be compiled (closure %s in %s)."
                        file line closureType.Name holder.FullName))
        | many ->
            raise (DlrTranslationException(
                    sprintf "%d dlr { } blocks share %s:%d; put each dlr { } on its own line." many.Length file line))
