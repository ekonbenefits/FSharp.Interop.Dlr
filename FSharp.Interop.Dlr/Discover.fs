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

    /// Every `builder.Run(builder.Delay(fun () -> body), file, line)` in `e` at this file and line.
    let rec private runsAt (builderType: Type) (file: string) (line: int) (e: Expr) : Expr list =
        match e with
        | Call(Some receiver, mi, [ Call(_, delay, [ Lambda(_, body) ]); Value(f, _); Value(l, _) ])
            when receiver.Type = builderType && mi.Name = "Run" && delay.Name = "Delay"
                 && unbox<int> l = line && unbox<string> f = file ->
            body :: runsAt builderType file line body
        | ShapeVar _ -> []
        | ShapeLambda(_, body) -> runsAt builderType file line body
        | ShapeCombination(_, args) -> args |> List.collect (runsAt builderType file line)

    /// What a block needs from its enclosing member's reflected definition.
    type Found =
        { /// Type declaring the enclosing member: the binder's accessibility context, as in C#.
          Context: Type
          /// The whole reflected body of the enclosing member, for resolving let-bound values
          /// the optimizer inlined instead of capturing.
          MemberBody: Expr
          /// The block's body (the Delay lambda's body).
          Body: Expr }

    /// The block whose Delay closure has type `closureType`, at `file:line`.
    let findBody (builderType: Type) (closureType: Type) (file: string) (line: int) : Found =
        let holder = if isNull closureType.DeclaringType then closureType else closureType.DeclaringType
        let search (types: seq<Type>) =
            types
            |> Seq.collect reflected
            |> Seq.collect (fun (m, q) -> runsAt builderType file line q |> List.map (fun b -> m, q, b))
            |> List.ofSeq
        let matches =
            match search [ holder ] with
            | [] ->
                // A member of a type declared in a namespace gets its closures nested in the file's
                // <StartupCode$…> class, not in the type: fall back to the whole assembly.
                closureType.Assembly.GetTypes() |> Seq.filter (fun t -> not t.IsNested) |> search
            | found -> found
        match matches with
        | [ m, memberBody, body ] -> { Context = m.DeclaringType; MemberBody = memberBody; Body = body }
        | [] ->
            raise (DlrTranslationException(
                    sprintf "dlr { } at %s:%d needs [<ReflectedDefinition>] on its enclosing module, type or member so the body can be compiled (closure %s in %s)."
                        file line closureType.Name holder.FullName))
        | many ->
            raise (DlrTranslationException(
                    sprintf "%d dlr { } blocks share %s:%d; put each dlr { } on its own line." many.Length file line))
