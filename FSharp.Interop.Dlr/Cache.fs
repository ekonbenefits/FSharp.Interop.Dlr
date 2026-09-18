namespace FSharp.Interop.Dlr

open System
open System.Collections.Concurrent

/// The compiled-delegate cache behind `dlr { }`, keyed by the Delay closure's compiler-generated
/// type, which is unique per block. Exposed for diagnostics and tests.
module DlrCache =

    let private cache = ConcurrentDictionary<Type, Translate.Compiled>()

    /// Listeners for `clear()`: the typed per-result caches.
    let internal onClear = ResizeArray<unit -> unit>()

    /// Number of compiled `dlr { }` sites.
    let count () = cache.Count

    /// Drops every compiled site; the next call at each site recompiles.
    let clear () =
        cache.Clear()
        lock onClear (fun () -> for f in onClear do f ())

    let internal getOrCompile (builderType: unit -> Type) (closureType: Type) (file: string) (line: int) (resultType: Type) =
        match cache.TryGetValue closureType with
        | true, compiled -> compiled
        | _ ->
            cache.GetOrAdd(closureType, fun t ->
                let builderType = builderType ()
                let found = Discover.findBody builderType t file line
                Translate.translate builderType found.Context found.MemberBody t resultType found.Body)

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

    static member Get(builder: obj, body: obj, file: string, line: int) : Func<obj, 'T> =
        let closureType = body.GetType()
        let generation = System.Threading.Volatile.Read &generation
        let hit = last
        if not (isNull hit) && obj.ReferenceEquals(hit.ClosureType, closureType) && hit.Generation = generation then hit.Func
        else
            match sites.TryGetValue closureType with
            | true, entry when entry.Generation = generation -> entry.Func
            | _ ->
                let compiled = DlrCache.getOrCompile (fun () -> builder.GetType()) closureType file line typeof<'T>
                let entry = SiteHit<'T>(closureType, compiled.Delegate :?> Func<obj, 'T>, generation)
                sites.[closureType] <- entry
                last <- entry
                entry.Func
