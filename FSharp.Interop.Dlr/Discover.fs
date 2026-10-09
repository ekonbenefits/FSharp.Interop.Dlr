namespace FSharp.Interop.Dlr

open System
open System.Collections.Concurrent
open System.Reflection
open FSharp.Quotations
open FSharp.Quotations.Patterns
open FSharp.Quotations.ExprShape

/// Finds the quoted body of a `dlr { }` block from the type of its state machine (Release) or its
/// Delay closure (Debug, and the one shape Release leaves to the closure), using the
/// `[<ReflectedDefinition>]` of the enclosing member.
module internal Discover =

    let private all =
        BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly

    /// Members with a reflected definition on `t` and every type nested in it (closures of a
    /// class member are nested in the enclosing module, not the class). Decoding a reflected
    /// definition costs microseconds and a module has one per member, so the result is kept
    /// per type: later blocks in the same type find their body without decoding again.
    let private reflectedCache = ConcurrentDictionary<Type, (MethodBase * Expr) list>()

    /// Members whose stored quotation FSharp.Core could not decode, per type: some
    /// compiler-generated members have one (a [<CLIEvent>] accessor), and so can a member of the
    /// user's whose body FSharp.Core refuses (`typeof<System.Void>` in it). Neither is fatal here
    /// — the block may be elsewhere — but when no body is found, the failure is the likely reason.
    let private undecodable = ConcurrentDictionary<Type, (MethodBase * exn) list>()

    let private tryReflected (m: MethodBase) =
        try Choice1Of2(Expr.TryGetReflectedDefinition m) with e -> Choice2Of2 e

    let rec private reflected (t: Type) : (MethodBase * Expr) list =
        reflectedCache.GetOrAdd(t, fun t ->
            let failures = ResizeArray()
            let found =
                [ for m in Seq.append (t.GetMethods all |> Seq.cast<MethodBase>) (t.GetConstructors all |> Seq.cast<MethodBase>) do
                      match tryReflected m with
                      | Choice1Of2(Some q) -> yield m, q
                      | Choice1Of2 None -> ()
                      | Choice2Of2 e -> failures.Add((m, e))
                  for n in t.GetNestedTypes all do
                      yield! reflected n ]
            if failures.Count > 0 then undecodable.[t] <- List.ofSeq failures
            found)

    /// The decode failures recorded for `types` and their nested types, for the not-found message.
    let rec private failuresIn (types: seq<Type>) : (MethodBase * exn) list =
        [ for t in types do
              match undecodable.TryGetValue t with
              | true, fs -> yield! fs
              | _ -> ()
              yield! failuresIn (t.GetNestedTypes all) ]

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
        let searched = ResizeArray<Type>()
        let matches =
            searched.Add holder
            match search [ holder ] with
            | [] ->
                // A member of a type declared in a namespace gets its closures nested in the file's
                // <StartupCode$…> class, not in the type: fall back to the whole assembly.
                let all = closureType.Assembly.GetTypes() |> Seq.filter (fun t -> not t.IsNested) |> List.ofSeq
                searched.AddRange all
                search all
            | found -> found
        let fromMap = lazy (BodyMap.find (CaptureMap.body closureType.Assembly) closureType file line)
        match matches with
        | [ m, memberBody, body ] ->
            let m, memberBody, body =
                match instantiate closureType m with
                | inst when obj.ReferenceEquals(inst, m) -> m, memberBody, body
                | inst ->
                    match tryReflected inst with
                    | Choice1Of2(Some q) ->
                        match runsAt builderType file line q with
                        | [ b ] -> inst, q, b
                        | _ -> m, memberBody, body
                    | _ -> m, memberBody, body
            { Context = m.DeclaringType; MemberBody = memberBody; Body = body }
        | [] when (match fromMap.Value with BodyMap.Found(_, q) -> List.length (runsAt builderType file line q) = 1 | _ -> false) ->
            // No reflected definition (or one FSharp.Core cannot decode), but the build companion's
            // map has the member: its body, rebuilt from the compiler's tree, is the same Expr.
            match fromMap.Value with
            | BodyMap.Found(context, memberBody) -> { Context = context; MemberBody = memberBody; Body = List.head (runsAt builderType file line memberBody) }
            | _ -> failwith "unreachable"
        | [] when (match fromMap.Value with BodyMap.Shared _ -> true | _ -> false) ->
            let n = match fromMap.Value with BodyMap.Shared n -> n | _ -> 0
            raise (DlrTranslationException(sprintf "%d dlr { } blocks share %s:%d; put each dlr { } on its own line." n file line))
        | [] ->
            // A member with the attribute whose quotation would not decode is the other reason a
            // body is not found — when it is the member the closure belongs to: the compiler
            // names a block's closure `<member>@<line>`.
            let undecoded =
                match failuresIn searched |> List.tryFind (fun (m, _) -> closureType.Name.StartsWith(m.Name + "@")) with
                | Some(m, e) -> sprintf " The block is in %s.%s, whose reflected definition could not be decoded (%s): its body has something a quotation cannot hold." m.DeclaringType.Name m.Name e.Message
                | None -> ""
            // A trimmed publish keeps the stored definitions but not what FSharp.Core matches them
            // by, so the block is not found although the attribute is there: say so where the
            // assembly holds reflected definitions at all.
            let trimmed =
                if holder.Assembly.GetManifestResourceNames() |> Array.exists (fun n -> n.StartsWith "ReflectedDefinitions") then
                    " If the attribute is there and the app is published trimmed (PublishTrimmed), trimming is the cause: it is not supported."
                else ""
            raise (DlrTranslationException(
                    sprintf "dlr { } at %s:%d needs [<ReflectedDefinition>] on the function or member that contains it, so its body can be compiled (closure %s in %s). Put the attribute on that one binding, not the whole module, unless everything in the module can be quoted.%s%s"
                        file line closureType.Name holder.FullName undecoded trimmed))
        | many ->
            raise (DlrTranslationException(
                    sprintf "%d dlr { } blocks share %s:%d; put each dlr { } on its own line." many.Length file line))
