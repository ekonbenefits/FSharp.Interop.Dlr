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

/// One site cache entry, immutable, so a reference to it is atomic to read and to swap.
[<AllowNullLiteral>]
type internal SiteHit<'T>(closureType: Type, f: Func<obj, 'T>) =
    member _.ClosureType = closureType
    member _.Func = f

/// The hot path's cache: per result type, the compiled `Func<obj, 'T>` by closure type, already
/// typed, so a call is one `GetType()`, one lookup and one invoke — no builder `GetType()`, no
/// cast. Misses fill it from `DlrCache` (which stays the one place that compiles and the one
/// `clear()` empties; a stale entry here is only a delegate that would have been recompiled the
/// same, so `clear()` clears these too).
type internal Sites<'T> private () =
    static let sites = ConcurrentDictionary<Type, Func<obj, 'T>>()
    /// The last site hit, as one immutable pair (an atomic reference): a block called repeatedly
    /// pays a reference compare instead of a hash lookup; alternating blocks fall through.
    static let mutable last : SiteHit<'T> = null
    /// Bumped by `clear()`: a miss that was compiling across a clear does not reinstall the
    /// pre-clear delegate, so "the next call recompiles" holds even then.
    static let mutable generation = 0
    static do lock DlrCache.onClear (fun () -> DlrCache.onClear.Add(fun () -> sites.Clear(); last <- null; System.Threading.Interlocked.Increment &generation |> ignore))

    static member Get(builder: obj, body: obj, file: string, line: int) : Func<obj, 'T> =
        let closureType = body.GetType()
        let hit = last
        if not (isNull hit) && obj.ReferenceEquals(hit.ClosureType, closureType) then hit.Func
        else
            let started = generation
            let f =
                match sites.TryGetValue closureType with
                | true, f -> f
                | _ ->
                    let compiled = DlrCache.getOrCompile (fun () -> builder.GetType()) closureType file line typeof<'T>
                    compiled.Delegate :?> Func<obj, 'T>
            if generation = started then
                sites.[closureType] <- f
                last <- SiteHit<'T>(closureType, f)
            f
