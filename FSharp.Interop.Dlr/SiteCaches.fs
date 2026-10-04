namespace FSharp.Interop.Dlr

open System
open System.Dynamic
open System.Linq.Expressions
open System.Reflection
open System.Runtime.CompilerServices
open Microsoft.CSharp.RuntimeBinder
open FSharp.Quotations

/// The call sites of an operation whose binder inputs are only known at run time (`(?) x name`
/// with `name` a variable): one set of sites per distinct key, created on first use from a
/// quotation template the translator built for the operation, and read back out of it. The
/// delegate that invokes them is compiled once, at translation time, with the sites as
/// parameters — so a new key costs the binders and sites (~µs, a few hundred bytes), not a
/// `LambdaExpression.Compile()`, and a repeated key costs one dictionary lookup. Bounded: at
/// `Capacity` entries the cache is cleared and refills, a miss being cheap.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type SiteCache<'Key when 'Key: equality>(template: 'Key -> Expr) =
    let entries = System.Collections.Concurrent.ConcurrentDictionary<'Key, CallSite[]>()

    /// The `CallSite` constants in a template's expression, one per distinct site, in traversal
    /// order — the order the translator's parameters follow.
    static member Sites(e: Expr) : CallSite list =
        let found = System.Collections.Generic.List<CallSite>()
        let rec walk (e: Expr) =
            match e with
            | Patterns.Value(v, _) ->
                match v with
                | :? CallSite as site when not (found |> Seq.exists (fun s -> obj.ReferenceEquals(s, site))) -> found.Add site
                | _ -> ()
            | ExprShape.ShapeVar _ -> ()
            | ExprShape.ShapeLambda(_, body) -> walk body
            | ExprShape.ShapeCombination(_, args) -> List.iter walk args
        walk e
        List.ofSeq found

    /// Entries kept per cache before it is cleared.
    static member val Capacity = 256 with get, set

    member _.Count = entries.Count

    member _.Get(key: 'Key) : CallSite[] =
        match entries.TryGetValue key with
        | true, sites -> sites
        | _ ->
            // Misses only: the admission (clear at capacity, then add) is one critical section,
            // so concurrent misses cannot each pass the check and push the count past the bound.
            lock entries (fun () ->
                match entries.TryGetValue key with
                | true, sites -> sites
                | _ ->
                    // A key from data: a null name or type is an argument error here, not a
                    // NullReferenceException from inside the binder.
                    match box key with
                    | :? (string * Type list) as k ->
                        let name, types = k
                        if isNull name then nullArg "a computed member name is null"
                        if isNull (box types) then nullArg "Dlr.typeArgsOf: the list is null"
                        if types |> List.exists isNull then invalidArg "types" "Dlr.typeArgsOf: a type in the list is null"
                    | _ -> ()
                    if entries.Count >= SiteCache<'Key>.Capacity then entries.Clear()
                    let sites = Array.ofList (SiteCache<'Key>.Sites(template key))
                    entries.[key] <- sites
                    sites)

    /// `sites.[i]`, for the quotation (array indexing has no direct quotation form the converter takes).
    static member At(sites: CallSite[], i: int) : CallSite = sites.[i]

/// One compiled `NamedOfCache` shape: its names (an empty name per positional), their hash, the delegate.
[<AllowNullLiteral; System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type NamedOfEntry(names: string[], hash: int, d: Delegate) =
    member _.Names = names
    member _.Hash = hash
    member _.Delegate = d

/// For `Dlr.argsOf` / `Dlr.namedOf`: the site's operation compiled once per distinct argument
/// shape — the ordered names, an empty name standing for a positional argument (`argsOf`
/// values first, then `namedOf` names) — since the shape changes the site's arity, so the
/// whole delegate is per key, not only its sites; bounded like `SiteCache`. The delegate takes
/// the target, the fixed arguments and the splatted values as one `obj[]` (positional, then
/// named).
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type NamedOfCache(compile: string list -> Delegate) =
    /// The entries by the hash of their names, an immutable snapshot replaced whole under the
    /// lock on a miss, so a lookup reads it without locking; the hash is computed in place from
    /// the argument lists, so a lookup allocates nothing.
    let mutable entries : System.Collections.Generic.Dictionary<int, NamedOfEntry list> = System.Collections.Generic.Dictionary()
    let mutable count = 0
    /// The last two shapes served: a site that repeats or alternates shapes hits here in one or
    /// two name compares, whatever the capacity.
    let mutable last : NamedOfEntry = null
    let mutable previous : NamedOfEntry = null

    /// Entries kept per cache before it is cleared: a miss is a `Compile()`. The lookup is a
    /// hash, so hits cost the same at any size; the bound is memory on a site that fills it
    /// (a shape is ~13 KB at one arity: 256 is ~3.5 MB), the same 256 as `SiteCache`.
    static member val Capacity = 256 with get, set

    /// The most positional arguments `Dlr.argsOf` accepts. Each distinct count is a new call-site
    /// arity — a delegate type (past Func's 17 type parameters, one emitted into a non-collectible dynamic
    /// assembly), a binder Microsoft.CSharp interns for the life of the process, a `Compile()` —
    /// so a count from data must not be unbounded, as a C# call site's arity is fixed by its
    /// source. A collection that could be long is one argument, not many.
    static member val MaxPositional = 64 with get, set

    member _.Count = count

    /// The hash of a shape: the positional count, then each name in order.
    static member private HashOf(positional: obj list, pairs: (string * obj) list) =
        let mutable h = 17
        for _ in positional do h <- h * 31
        for (n, _) in pairs do h <- h * 31 + (if isNull n then 0 else n.GetHashCode())
        h

    /// Whether an entry's names are the shape of these arguments: `positional.Length` empty
    /// names, then the pairs' names in order. Loops, no closures: the hit path allocates nothing.
    static member private Matches(names: string[], positional: obj list, pairs: (string * obj) list) =
        let mutable i = 0
        let mutable ok = true
        let mutable ps = positional
        while ok && not ps.IsEmpty do
            if i < names.Length && names.[i].Length = 0 then i <- i + 1; ps <- ps.Tail
            else ok <- false
        let mutable rest = pairs
        while ok && not rest.IsEmpty do
            let (n, _) = rest.Head
            // An empty name is the positional marker in `names`: a pair never matches one.
            if i < names.Length && not (String.IsNullOrEmpty n) && String.Equals(n, names.[i]) then i <- i + 1; rest <- rest.Tail
            else ok <- false
        ok && i = names.Length

    /// The entry of this shape in a bucket, or null: a loop, no closure, no option, so the hash
    /// path allocates nothing.
    static member private Find(bucket: NamedOfEntry list, hash: int, positional: obj list, pairs: (string * obj) list) : NamedOfEntry =
        let mutable rest = bucket
        let mutable found : NamedOfEntry = null
        while isNull found && not rest.IsEmpty do
            let e = rest.Head
            if e.Hash = hash && NamedOfCache.Matches(e.Names, positional, pairs) then found <- e
            rest <- rest.Tail
        found

    /// The delegate for these arguments' shape.
    member this.Get(positional: obj list, pairs: (string * obj) list) : Delegate =
        if isNull (box positional) then nullArg "Dlr.argsOf: the list is null"
        if isNull (box pairs) then nullArg "Dlr.namedOf: the list is null"
        // The last two shapes served, compared by name first: cheaper than hashing the names.
        let l = last
        if not (isNull l) && NamedOfCache.Matches(l.Names, positional, pairs) then l.Delegate
        else
        let p = previous
        if not (isNull p) && NamedOfCache.Matches(p.Names, positional, pairs) then previous <- l; last <- p; p.Delegate
        else
        let hash = NamedOfCache.HashOf(positional, pairs)
        let mutable bucket = Unchecked.defaultof<NamedOfEntry list>   // out parameter: no tuple
        let hit = if entries.TryGetValue(hash, &bucket) then NamedOfCache.Find(bucket, hash, positional, pairs) else null
        if not (isNull hit) then previous <- last; last <- hit; hit.Delegate
        else
            lock this (fun () ->
                let current = entries
                let again = match current.TryGetValue hash with | true, b -> NamedOfCache.Find(b, hash, positional, pairs) | _ -> null
                if not (isNull again) then previous <- last; last <- again; again.Delegate
                else
                    // Names from data: a null or empty one would be taken for a positional slot.
                    for (n, _) in pairs do
                        if isNull n then nullArg "Dlr.namedOf: an argument name is null"
                        if n.Length = 0 then invalidArg "pairs" "Dlr.namedOf: an argument name is empty (positional arguments from data are Dlr.argsOf)"
                    let positionalCount = List.length positional
                    if positionalCount > NamedOfCache.MaxPositional then
                        invalidArg "positional" (sprintf "Dlr.argsOf: %d positional arguments; at most %d. Each distinct count is a call-site shape compiled and kept for the life of the process, so a collection that could be long is one argument (an array to a params parameter, a list), not many." positionalCount NamedOfCache.MaxPositional)
                    let names = (positional |> List.map (fun _ -> "")) @ (pairs |> List.map fst)
                    let e = NamedOfEntry(Array.ofList names, hash, compile names)
                    let next =
                        if count >= NamedOfCache.Capacity then (count <- 0; System.Collections.Generic.Dictionary())
                        else System.Collections.Generic.Dictionary(current)
                    next.[hash] <- e :: (match next.TryGetValue hash with | true, b -> b | _ -> [])
                    count <- count + 1
                    entries <- next
                    previous <- last
                    last <- e
                    e.Delegate)

    /// The splatted values, positional then named, for the quotation.
    static member Values(positional: obj list, pairs: (string * obj) list) : obj[] =
        let values = Array.zeroCreate (List.length positional + List.length pairs)
        let mutable i = 0
        for v in positional do
            values.[i] <- v
            i <- i + 1
        for (_, v) in pairs do
            values.[i] <- v
            i <- i + 1
        values
    static member At(values: obj[], i: int) : obj = values.[i]
