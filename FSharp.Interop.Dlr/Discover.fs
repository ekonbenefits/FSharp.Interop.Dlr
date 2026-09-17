namespace FSharp.Interop.Dlr

open System
open System.Collections.Concurrent
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
    /// class member are nested in the enclosing module, not the class). Decoding a reflected
    /// definition costs microseconds and a module has one per member, so the result is kept
    /// per type: later blocks in the same type find their body without decoding again.
    let private reflectedCache = ConcurrentDictionary<Type, (MethodBase * Expr) list>()

    let rec private reflected (t: Type) : (MethodBase * Expr) list =
        reflectedCache.GetOrAdd(t, fun t ->
            [ for m in t.GetMethods all do
                  match Expr.TryGetReflectedDefinition m with
                  | Some q -> yield (m :> MethodBase), q
                  | None -> ()
              for c in t.GetConstructors all do
                  match Expr.TryGetReflectedDefinition c with
                  | Some q -> yield (c :> MethodBase), q
                  | None -> ()
              for n in t.GetNestedTypes all do
                  yield! reflected n ])

    /// Every `builder.Run(builder.Delay(fun () -> body), file, line)` in `e` at this file and line.
    let rec private runsAt (builderType: Type) (file: string) (line: int) (e: Expr) : Expr list =
        match e with
        | Call(Some receiver, mi, [ Call(_, delay, [ Lambda(_, body) ]); Value(f, _); Value(l, _) ])
            when receiver.Type = builderType && mi.Name = "Run" && delay.Name = "Delay"
                 && unbox<int> l = line && unbox<string> f = file ->
            // Blocks nested inside this one compile with it (see Translate), so do not list them.
            [ body ]
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

    /// For a block inside a generic member, the closure class carries the member's type parameters
    /// under the same names, instantiated. Rebuild the member with those arguments so its reflected
    /// definition comes back with concrete types. A parameter the closure does not carry is unused
    /// by the block, so any instantiation will do.
    let private instantiate (closureType: Type) (m: MethodBase) : MethodBase =
        if not (m.IsGenericMethodDefinition || m.DeclaringType.IsGenericTypeDefinition) then m
        else
            let concrete =
                if closureType.IsGenericType then
                    Array.zip (closureType.GetGenericTypeDefinition().GetGenericArguments()) (closureType.GetGenericArguments())
                    |> Array.map (fun (p, a) -> p.Name, a)
                    |> dict
                else dict []
            let resolve (p: Type) = match concrete.TryGetValue p.Name with | true, t -> t | _ -> typeof<obj>
            let onType =
                if m.DeclaringType.IsGenericTypeDefinition then
                    let constructed = m.DeclaringType.MakeGenericType(m.DeclaringType.GetGenericArguments() |> Array.map resolve)
                    MethodBase.GetMethodFromHandle(m.MethodHandle, constructed.TypeHandle)
                else m
            match onType with
            | :? MethodInfo as mi when mi.IsGenericMethodDefinition ->
                mi.MakeGenericMethod(mi.GetGenericArguments() |> Array.map resolve) :> MethodBase
            | other -> other

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
        | [ m, memberBody, body ] ->
            let m, memberBody, body =
                match instantiate closureType m with
                | inst when obj.ReferenceEquals(inst, m) -> m, memberBody, body
                | inst ->
                    match Expr.TryGetReflectedDefinition inst with
                    | Some q ->
                        match runsAt builderType file line q with
                        | [ b ] -> inst, q, b
                        | _ -> m, memberBody, body
                    | None -> m, memberBody, body
            { Context = m.DeclaringType; MemberBody = memberBody; Body = body }
        | [] ->
            raise (DlrTranslationException(
                    sprintf "dlr { } at %s:%d needs [<ReflectedDefinition>] on the function or member that contains it, so its body can be compiled (closure %s in %s). Put the attribute on that one binding, not the whole module, unless everything in the module can be quoted."
                        file line closureType.Name holder.FullName))
        | many ->
            raise (DlrTranslationException(
                    sprintf "%d dlr { } blocks share %s:%d; put each dlr { } on its own line." many.Length file line))
