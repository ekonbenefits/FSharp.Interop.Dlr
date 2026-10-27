namespace FSharp.Interop.Dlr

open System
open System.Collections.Concurrent
open System.Reflection
open FSharp.Quotations
open FSharp.Quotations.Patterns
open FSharp.Quotations.ExprShape

/// Finds the quoted body of a `dlr { }` block from the type of its state machine (Release) or its
/// Delay closure (Debug on the 10.0 compiler, and the one shape Release leaves to the closure), using the
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

    /// Every `builder.Run(builder.Delay((fun () -> body), file, line))` in `e` at this file and line.
    let rec private runsAt (builderType: Type) (file: string) (line: int) (e: Expr) : Expr list =
        match e with
        | Call(Some receiver, mi, [ Call(_, delay, [ Lambda(_, body); Value(f, _); Value(l, _) ]) ])
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
    /// (and its type's) that it uses, under the same names, instantiated. Rebuild the member with
    /// those arguments so its reflected definition comes back with concrete types. A parameter the
    /// closure does not carry is unused by the block, so any instantiation will do: `obj`, or,
    /// where a constraint rejects `obj` (#222), the parameter itself, left open.
    let private instantiate (closureType: Type) (m: MethodBase) : MethodBase =
        if not (m.IsGenericMethodDefinition || m.DeclaringType.IsGenericTypeDefinition) then m
        else
            let concrete =
                if closureType.IsGenericType then
                    Array.zip (closureType.GetGenericTypeDefinition().GetGenericArguments()) (closureType.GetGenericArguments())
                    |> Array.map (fun (p, a) -> p.Name, a)
                    |> dict
                else dict []
            let build (unused: Type -> Type) =
                let resolve (p: Type) = match concrete.TryGetValue p.Name with | true, t -> t | _ -> unused p
                let onType =
                    if m.DeclaringType.IsGenericTypeDefinition then
                        let constructed = m.DeclaringType.MakeGenericType(m.DeclaringType.GetGenericArguments() |> Array.map resolve)
                        MethodBase.GetMethodFromHandle(m.MethodHandle, constructed.TypeHandle)
                    else m
                match onType with
                | :? MethodInfo as mi when mi.IsGenericMethodDefinition ->
                    mi.MakeGenericMethod(mi.GetGenericArguments() |> Array.map resolve) :> MethodBase
                | other -> other
            try build (fun _ -> typeof<obj>)
            with :? ArgumentException -> build id

    /// A body with its variables renamed canonically, so two trees compare equal exactly when they
    /// are the same up to their variables: each binder by its position, each free variable by its
    /// name, type and mutability. `shared` gives both trees the same `Var` for the same name, as
    /// `Expr` compares variables by reference. Two free variables of one name in one tree would
    /// become one; the reflected definition is the unoptimized source, where a block cannot see
    /// two variables of one name (the inner one shadows the outer), so that does not arise.
    let private canonical (shared: System.Collections.Generic.Dictionary<string, Var>) (e: Expr) : Expr =
        let var (key: string) (t: Type) (isMutable: bool) =
            let key = sprintf "%s:%O:%b" key t isMutable
            match shared.TryGetValue key with
            | true, v -> v
            | _ -> let v = Var(key, t, isMutable) in shared.[key] <- v; v
        let mutable binders = 0
        let rec go (bound: Map<Var, Var>) (e: Expr) =
            match e with
            | ShapeVar v ->
                match bound.TryFind v with
                | Some c -> Expr.Var c
                | None -> Expr.Var(var ("free " + v.Name) v.Type v.IsMutable)
            | ShapeLambda(v, body) ->
                binders <- binders + 1
                let c = var (sprintf "bound %d" binders) v.Type v.IsMutable
                Expr.Lambda(c, go (bound.Add(v, c)) body)
            | _ -> Quotation.rebuild (go bound) e
        go Map.empty e

    /// The one block `bodies` are, when they are several copies of it. The quotation holds a match
    /// arm whose or-pattern binds a variable once per alternative, each copy over its own `Var`s,
    /// so the block is found once per alternative at its line (#263). Copies are taken as one block
    /// only when it cannot matter which is used: the same up to their variables, and every variable
    /// the block reads either a field of its own container (as the translator sees them), read by
    /// name where the translator reads it so (not an alias it substitutes), or one whose recovered definition is the same in every copy up to its variables, and
    /// reaches only such variables in turn (#271): the translator recovers it from the first copy,
    /// and the copy that ran would give the same. A variable whose definitions differ (an inlined
    /// pattern variable, `Choice1Of2`'s field in one copy and `Choice2Of2`'s in another; another
    /// block's look-alike on the same line) keeps the copies refused. The builder is never
    /// captured (the translator drops it by type, as here). `Error` names the variables that kept
    /// copies from being taken as one, or is empty when the runs are not copies at all.
    let private oneOf (builderType: Type) (closureType: Type) (memberBody: Expr) (bodies: Expr list) : Result<Expr, string list> =
        match bodies with
        | [ body ] -> Ok body
        | first :: rest ->
            let shared = System.Collections.Generic.Dictionary<string, Var>()
            let shape = canonical shared first
            if not (rest |> List.forall (fun b -> canonical shared b = shape)) then Error []
            else
                // The translator's own view of the container's fields: a machine's `Data` and
                // `ResumptionPoint` are not captures, though a variable may be named either.
                let fields = TranslateBlock.Captures.fields closureType
                // Per tree, its free variables by name: None when two share one (not the unoptimized
                // source's, where the inner shadows the outer).
                let byName (e: Expr) =
                    let vs = e.GetFreeVars() |> List.ofSeq
                    let names = vs |> List.map (fun v -> v.Name)
                    if List.length (List.distinct names) = names.Length then Some(dict [ for v in vs -> v.Name, v ]) else None
                // Per copy, its own free variables, which the translator never takes for aliases.
                let owns = bodies |> List.map (fun b -> b.GetFreeVars() |> Set.ofSeq)
                // Whether it cannot matter which copy's variable this is (`vars`, one per copy, of one name).
                let rec agree (vars: Var list) =
                    let v = List.head vars
                    v.Type = builderType
                    // Read from its field, as the translator reads it: not an alias it substitutes,
                    // which a field of its name may sit beside, holding another variable (#272 review).
                    || (fields.ContainsKey v.Name && not (List.exists2 (fun own w -> TranslateBlock.Captures.isAlias own memberBody w) owns vars))
                    // Bound outside the copies: one variable, one definition.
                    || vars |> List.forall (fun w -> w = v)
                    || (let defs = vars |> List.map (fun w -> TranslatePatterns.letDefinition w memberBody |> Option.orElse (TranslatePatterns.parameterArgument w memberBody))
                        match List.choose id defs with
                        | first :: rest when rest.Length = defs.Length - 1 ->
                            let shared = System.Collections.Generic.Dictionary<string, Var>()
                            let shape = canonical shared first
                            rest |> List.forall (fun d -> canonical shared d = shape)
                            && (match List.choose byName (first :: rest) with
                                | reached :: others when others.Length = rest.Length ->
                                    reached.Keys |> Seq.forall (fun n ->
                                        others |> List.forall (fun o -> o.ContainsKey n)
                                        && agree (reached.[n] :: [ for o in others -> o.[n] ]))
                                | _ -> false)
                        | _ -> false)
                match List.choose byName bodies with
                | copies when copies.Length = bodies.Length ->
                    let firstVars = List.head copies
                    let uncaptured =
                        firstVars.Keys
                        |> Seq.filter (fun n ->
                            let vars = [ for c in copies -> match c.TryGetValue n with | true, v -> Some v | _ -> None ]
                            not (List.forall Option.isSome vars && agree (List.choose id vars)))
                        |> List.ofSeq
                    match uncaptured with
                    | [] -> Ok first
                    | uncaptured -> Error uncaptured
                // Two free variables of one name in a body: not seen in a reflected one (an inner
                // binding shadows), kept so that it would be refused, not guessed.
                | _ -> Error [ for v in first.GetFreeVars() -> v.Name ]
        | [] -> Error []

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
        // Copies of one block in one member (an or-pattern's arm) are that block, not several.
        let matches =
            match matches with
            | (m, memberBody, _) :: rest when not rest.IsEmpty && rest |> List.forall (fun (m', _, _) -> m' = m) ->
                match oneOf builderType closureType memberBody (matches |> List.map (fun (_, _, b) -> b)) with
                | Ok body -> [ m, memberBody, body ]
                | Error [] -> matches
                | Error uncaptured ->
                    raise (DlrTranslationException(
                            sprintf "dlr { } at %s:%d is held %d times in its member's reflected definition (an or-pattern's arm, or look-alike blocks on one line), and reads %s, which the optimizer did not capture as itself (fields: %s), so which copy is meant cannot be told. Bind it to a value the block reads directly, or move the block into its own function."
                                file line matches.Length (uncaptured |> List.map (sprintf "'%s'") |> String.concat ", ")
                                (String.Join(", ", (TranslateBlock.Captures.fields closureType).Keys))))
            | _ -> matches
        match matches with
        | [ m, memberBody, body ] ->
            let m, memberBody, body =
                match instantiate closureType m with
                | inst when obj.ReferenceEquals(inst, m) -> m, memberBody, body
                | inst ->
                    match tryReflected inst with
                    | Choice1Of2(Some q) ->
                        match oneOf builderType closureType q (runsAt builderType file line q) with
                        | Ok b -> inst, q, b
                        | Error _ -> m, memberBody, body
                    | _ -> m, memberBody, body
            { Context = m.DeclaringType; MemberBody = memberBody; Body = body }
        | [] ->
            // The block's own container never has a reflected definition, so decoding one of its
            // methods throws only when the assembly's definitions cannot be read at all: FSharp.Core
            // reads them together, and one it cannot bind (a function a signature hides, erased by
            // Release inlining, #213) fails every method of every type. A body it refuses
            // (`typeof<System.Void>`) fails only its own member. Told apart by this, not by
            // FSharp.Core's (localized) message, which for Void names no member.
            let assemblyWide =
                closureType.GetMethods all
                |> Seq.tryPick (fun m -> match tryReflected m with Choice2Of2 e -> Some e | _ -> None)
            // A member with the attribute whose quotation would not decode is the other reason a
            // body is not found — when it is the member the closure belongs to: the compiler
            // names a block's closure `<member>@<line>`. A name alone also matches a same-named
            // member of another type (#248), so this is stated only for a member declared in the
            // type holding the closure. A class's closures are nested in its module, which its
            // sibling classes share, so a class member is left to the hint below, whose wording
            // allows for a wrong guess, like any other match.
            let own = failuresIn searched |> List.tryFind (fun (m, _) -> closureType.Name.StartsWith(m.Name + "@") && m.DeclaringType = holder)
            match assemblyWide, own with
            | Some e, _ ->
                raise (DlrTranslationException(
                        sprintf "dlr { } at %s:%d cannot find its body: FSharp.Core could not decode any of %s's reflected definitions (%s), so every block in the assembly fails, wherever it is (closure %s in %s). FSharp.Core reads an assembly's reflected definitions together, and one it cannot bind fails them all. The usual cause is a function a signature file (.fsi) leaves out, called from a reflected definition: the Release optimizer inlines and erases it, and the stored quotation still names it. Add that function (the one FSharp.Core names) to the signature, or mark it [<MethodImpl(MethodImplOptions.NoInlining)>]; the analyzer reports it at build time (DLR008)."
                            file line (holder.Assembly.GetName().Name) e.Message closureType.Name holder.FullName))
            | None, Some(m, e) ->
                raise (DlrTranslationException(
                        sprintf "dlr { } at %s:%d is in %s.%s, whose reflected definition FSharp.Core could not decode (%s), so the block's body cannot be found (closure %s in %s). Its body has something a quotation cannot hold."
                            file line m.DeclaringType.Name m.Name e.Message closureType.Name holder.FullName))
            | None, None ->
                // A block in a local function has a closure named after the local, not the member
                // (Debug; Release names the machine after the member), so the member is unknown
                // here (#245). Members that failed to decode in the closure's type and the types
                // nested in it (a class in the module) are then the likely place. A type declared in
                // a namespace has its closures in the file's <StartupCode$…> class, which nothing
                // ties to the types the file declares: there, the assembly's. The attribute may still
                // be missing, so they are a hint after the missing-attribute message, not a
                // replacement for it.
                let candidates =
                    if not (isNull holder.Namespace) && holder.Namespace.StartsWith "<StartupCode$" then failuresIn searched
                    else failuresIn [ holder ]
                    // `searched` holds the closure's type twice when the assembly-wide fallback ran,
                    // and a nested type comes once more through its parent.
                    |> List.distinctBy fst
                let hint =
                    match candidates with
                    | [] ->
                        // A trimmed publish keeps the stored definitions but not what FSharp.Core
                        // matches them by, so the block is not found although the attribute is
                        // there: say so where the assembly holds reflected definitions at all.
                        if holder.Assembly.GetManifestResourceNames() |> Array.exists (fun n -> n.StartsWith "ReflectedDefinitions") then
                            " If the attribute is there and the app is published trimmed (PublishTrimmed), trimming is the cause: it is not supported."
                        else ""
                    | [ m, e ] ->
                        sprintf " If the block is in %s.%s (in a local function there, say), the attribute is there, but FSharp.Core could not decode that member's reflected definition (%s): its body has something a quotation cannot hold."
                            m.DeclaringType.Name m.Name e.Message
                    | several ->
                        // One error per distinct message, with the members it was raised for.
                        let named =
                            several
                            |> List.groupBy (fun (_, e) -> e.Message)
                            |> List.map (fun (message, ms) -> sprintf "%s (%s)" (ms |> List.map (fun (m, _) -> m.DeclaringType.Name + "." + m.Name) |> String.concat ", ") message)
                            |> String.concat "; "
                        sprintf " If the block is in one of %s (in a local function there, say), the attribute is there, but FSharp.Core could not decode that member's reflected definition: its body has something a quotation cannot hold."
                            named
                raise (DlrTranslationException(
                        sprintf "dlr { } at %s:%d needs [<ReflectedDefinition>] on the function or member that contains it, so its body can be compiled (closure %s in %s). Put the attribute on that one binding, not the whole module, unless everything in the module can be quoted.%s"
                            file line closureType.Name holder.FullName hint))
        | many ->
            raise (DlrTranslationException(
                    sprintf "%d dlr { } blocks share %s:%d; put each dlr { } on its own line." many.Length file line))
