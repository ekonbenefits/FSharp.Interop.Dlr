namespace FSharp.Interop.Dlr

open System
open System.Collections.Concurrent

/// The compiled-delegate cache behind `dlr { }`, keyed by the Delay closure's compiler-generated
/// type, which is unique per block, and behind `dlrq { }`, keyed by the block's file and line and
/// the shape of its quotation. Exposed for diagnostics and tests.
module DlrCache =

    let private cache = ConcurrentDictionary<Type, Translate.Compiled>()

    /// Listeners for `clear()`: the typed per-result caches.
    let internal onClear = ResizeArray<unit -> unit>()
    /// The typed `dlrq { }` caches report their sizes through these.
    let internal counters = ResizeArray<unit -> int>()

    /// Number of compiled sites, `dlr { }` and `dlrq { }` together.
    let count () = cache.Count + lock counters (fun () -> counters |> Seq.sumBy (fun f -> f ()))

    /// Drops every compiled site; the next call at each site recompiles.
    let clear () =
        cache.Clear()
        lock onClear (fun () -> for f in onClear do f ())

    /// The builders as the translator needs them: the caller's own first, for `Discover`.
    let internal getOrCompile (builderTypes: Type list) (closureType: Type) (file: string) (line: int) (resultType: Type) =
        match cache.TryGetValue closureType with
        | true, compiled -> compiled
        | _ ->
            cache.GetOrAdd(closureType, fun t ->
                let found = Discover.findBody (List.head builderTypes) t file line
                Translate.translate builderTypes found.Context found.MemberBody (FSharp.Quotations.Var("closure", typeof<obj>)) t resultType found.Body)

    /// A `dlrq { }` body over its slot array. No enclosing member is known, so the binder's
    /// accessibility context is `obj`: public members only.
    let internal compileQuoted (builderTypes: Type list) (q: FSharp.Quotations.Expr) (resultType: Type) =
        let slots = FSharp.Quotations.Var("slots", typeof<obj[]>)
        let body = Quoted.prepare (List.head builderTypes) slots (Quoted.body q)
        Translate.translate builderTypes typeof<obj> body slots typeof<obj[]> resultType body

/// One site cache entry, immutable, so a reference to it is atomic to read and to swap. Stamped
/// with the clear generation it was compiled under: an entry from before a `clear()` is never
/// served, however it got installed.
[<AllowNullLiteral>]
type internal SiteHit<'T>(closureType: Type, f: Func<obj, 'T>, generation: int) =
    member _.ClosureType = closureType
    member _.Func = f
    member _.Generation = generation

/// The hot path's cache: per result type, the compiled `Func<obj, 'T>` by closure type, already
/// typed, so a call is one `GetType()`, one compare or lookup and one invoke — no builder
/// `GetType()`, no cast. Misses fill it from `DlrCache`, which stays the one place that compiles.
type internal Sites<'T> private () =
    static let sites = ConcurrentDictionary<Type, SiteHit<'T>>()
    /// The last block *compiled* (an atomic reference to an immutable entry): a block called
    /// repeatedly pays a reference compare instead of a hash lookup. Installed only on a miss, so
    /// two blocks called in turn cost a lookup each and never write.
    static let mutable last : SiteHit<'T> = null
    /// The clear generation: `clear()` bumps it, and an entry is served only if it was compiled
    /// under the current one. Clearing the dictionary is housekeeping; the stamp is the guarantee.
    static let mutable generation = 0
    static do lock DlrCache.onClear (fun () -> DlrCache.onClear.Add(fun () -> System.Threading.Interlocked.Increment &generation |> ignore; sites.Clear(); last <- null))

    static member Get(body: obj, builderTypes: Type list, file: string, line: int) : Func<obj, 'T> =
        let closureType = body.GetType()
        let generation = System.Threading.Volatile.Read &generation
        let hit = last
        if not (isNull hit) && obj.ReferenceEquals(hit.ClosureType, closureType) && hit.Generation = generation then hit.Func
        else
            match sites.TryGetValue closureType with
            | true, entry when entry.Generation = generation -> entry.Func
            | _ ->
                let compiled = DlrCache.getOrCompile builderTypes closureType file line typeof<'T>
                let entry = SiteHit<'T>(closureType, compiled.Delegate :?> Func<obj, 'T>, generation)
                sites.[closureType] <- entry
                last <- entry
                entry.Func

/// One `dlrq { }` cache entry: the shape it was compiled for and the typed delegate.
[<AllowNullLiteral>]
type internal QuotedHit<'T>(shape: obj[], f: Func<obj[], 'T>, generation: int) =
    member _.Shape = shape
    member _.Func = f
    member _.Generation = generation

/// The `dlrq { }` cache, per result type: by file and line, the entries compiled at that site —
/// usually one; several when a generic enclosing function instantiates the block with different
/// types, told apart by shape. Bounded per site so a runaway generic cannot grow it.
type internal QuotedSites<'T> private () =
    static let sites = ConcurrentDictionary<struct (string * int), QuotedHit<'T>[]>()
    static let mutable generation = 0
    static let perSite = 8
    static do
        lock DlrCache.onClear (fun () -> DlrCache.onClear.Add(fun () -> System.Threading.Interlocked.Increment &generation |> ignore; sites.Clear()))
        lock DlrCache.counters (fun () -> DlrCache.counters.Add(fun () -> sites.Values |> Seq.sumBy Array.length))

    static member Get(builderTypes: Type list, q: FSharp.Quotations.Expr, file: string, line: int) : 'T =
        let slots, shape = Quoted.scan (List.head builderTypes) q
        let generation = System.Threading.Volatile.Read &generation
        let key = struct (file, line)
        let hits = match sites.TryGetValue key with | true, hits -> hits | _ -> [||]
        match hits |> Array.tryFind (fun h -> h.Generation = generation && Quoted.sameShape h.Shape shape) with
        | Some hit -> hit.Func.Invoke slots
        | None ->
            let compiled = DlrCache.compileQuoted builderTypes q typeof<'T>
            let hit = QuotedHit<'T>(shape, compiled.Delegate :?> Func<obj[], 'T>, generation)
            sites.AddOrUpdate(key, [| hit |], fun _ old ->
                let kept = old |> Array.filter (fun h -> h.Generation = generation)
                Array.append (if kept.Length >= perSite then Array.skip 1 kept else kept) [| hit |]) |> ignore
            hit.Func.Invoke slots
