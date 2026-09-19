namespace FSharp.Interop.Dlr

open System
open System.Collections.Concurrent
open System.Reflection
open System.Threading
open Microsoft.FSharp.Core.CompilerServices

/// The compiled-delegate cache behind `dlr { }`, keyed by the block's compiler-generated type
/// (its state machine struct, or in the fallback path its Delay closure), which is unique per
/// block. Exposed for diagnostics and tests.
module DlrCache =

    let private cache = ConcurrentDictionary<Type, Translate.Compiled>()

    /// The clear generation: `clear()` bumps it, and the typed caches serve an entry only if it
    /// was compiled under the current one, however it got installed. Clearing their slots is
    /// housekeeping; the stamp is the guarantee.
    let mutable internal generation = 0

    /// Listeners for `clear()`: the typed per-site caches drop their entries.
    let internal onClear = ResizeArray<unit -> unit>()

    /// Number of compiled `dlr { }` sites.
    let count () = cache.Count

    /// Drops every compiled site; the next call at each site recompiles. The dictionary is
    /// cleared *before* the generation moves: a miss that reads the new generation must not be
    /// able to find a pre-clear entry in the dictionary and install it stamped as current.
    let clear () =
        cache.Clear()
        Interlocked.Increment &generation |> ignore
        lock onClear (fun () -> for f in onClear do f ())

    let internal getOrCompile (builderType: unit -> Type) (blockType: Type) (file: string) (line: int) (resultType: Type) =
        match cache.TryGetValue blockType with
        | true, compiled -> compiled
        | _ ->
            cache.GetOrAdd(blockType, fun t ->
                let builderType = builderType ()
                let found = Discover.findBody builderType t file line
                Translate.translate builderType found.Context found.MemberBody t resultType found.Body)

/// One compiled-machine cache entry, immutable, so a reference to it is atomic to read and to
/// swap, stamped with the clear generation it was compiled under.
[<AllowNullLiteral>]
type internal MachineHit<'SM, 'T>(reader: DlrReader<'SM, 'T>, generation: int) =
    member _.Reader = reader
    member _.Generation = generation

/// The hot path: one static slot per state machine type (per block, per instantiation of a
/// generic member), so a call is a static field read, a generation compare and one invoke by
/// reference. No `GetType()`, no lookup, no cast. A plain static field (no `static let`) so
/// there is no initialization check on the read; the clear listener is registered on the first
/// compile instead. Misses fill it from `DlrCache`, which stays the one place that compiles.
type internal Machines<'SM, 'T> private () =
    [<DefaultValue>]
    static val mutable private hit : MachineHit<'SM, 'T>
    [<DefaultValue>]
    static val mutable private registered : bool

    static member Get(builder: obj, file: string, line: int) : DlrReader<'SM, 'T> =
        let hit = Machines<'SM, 'T>.hit
        if not (isNull hit) && hit.Generation = DlrCache.generation then hit.Reader
        else Machines<'SM, 'T>.Miss(builder, file, line)

    static member private Miss(builder: obj, file: string, line: int) : DlrReader<'SM, 'T> =
        let generation = Volatile.Read &DlrCache.generation
        let compiled = DlrCache.getOrCompile (fun () -> builder.GetType()) typeof<'SM> file line typeof<'T>
        let entry = MachineHit<'SM, 'T>(compiled.Delegate :?> DlrReader<'SM, 'T>, generation)
        lock DlrCache.onClear (fun () ->
            if not Machines<'SM, 'T>.registered then
                Machines<'SM, 'T>.registered <- true
                DlrCache.onClear.Add(fun () -> Machines<'SM, 'T>.hit <- null))
        Machines<'SM, 'T>.hit <- entry
        entry.Reader

/// One closure-path cache entry, immutable and generation-stamped like `MachineHit`.
[<AllowNullLiteral>]
type internal SiteHit<'T>(closureType: Type, f: Func<obj, 'T>, generation: int) =
    member _.ClosureType = closureType
    member _.Func = f
    member _.Generation = generation

/// The fallback path's cache, for blocks the compiler did not turn into a state machine (Debug
/// builds): per result type, the compiled `Func<obj, 'T>` by Delay-closure type, already typed,
/// so a call is one `GetType()`, one compare or lookup and one invoke.
type internal Sites<'T> private () =
    static let sites = ConcurrentDictionary<Type, SiteHit<'T>>()
    /// The last block *compiled* (an atomic reference to an immutable entry): a block called
    /// repeatedly pays a reference compare instead of a hash lookup. Installed only on a miss, so
    /// two blocks called in turn cost a lookup each and never write.
    static let mutable last : SiteHit<'T> = null
    static do lock DlrCache.onClear (fun () -> DlrCache.onClear.Add(fun () -> sites.Clear(); last <- null))

    static member Get(builder: obj, closure: obj, file: string, line: int) : Func<obj, 'T> =
        let closureType = closure.GetType()
        let generation = Volatile.Read &DlrCache.generation
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

/// <summary>The entry points the inlined <c>Run</c> compiles to. Not for direct use.</summary>
[<Sealed; AbstractClass>]
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type DlrRun =

    /// <summary>The state machine path: <typeparamref name="SM"/> is the block's struct, a JIT constant here.</summary>
    static member Machine<'SM, 'T>(builder: obj, sm: byref<'SM>, file: string, line: int) : 'T =
        Machines<'SM, 'T>.Get(builder, file, line).Invoke(&sm)

    /// Per delegate-target type: the field holding the Delay closure, or null when the compiler
    /// inlined the closure into the target (then the target is the block's closure itself).
    static member val private delayedFields = ConcurrentDictionary<Type, FieldInfo>()

    /// <summary>The fallback path (no statically compiled state machine, e.g. Debug): the block's Delay closure is read back out of the resumable-code delegate.</summary>
    static member Closure<'T>(builder: obj, code: ResumableCode<DlrData<'T>, 'T>, file: string, line: int) : 'T =
        let target = code.Target
        if isNull target then
            // An optimized build inlined the Delay closure and left a static delegate: only a
            // site the compiler could not turn into a state machine (FS3511) gets here.
            raise (DlrTranslationException(sprintf "dlr { } at %s:%d could not be compiled as a state machine (the compiler reported FS3511 at the site) and left no closure to compile from; write the block as dlr { … } rather than calling the builder's members directly." file line))
        let field =
            DlrRun.delayedFields.GetOrAdd(target.GetType(), fun t ->
                match t.GetField("delayed", BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) with
                | null -> null
                | f when f.FieldType = typeof<unit -> ResumableCode<DlrData<'T>, 'T>> -> f
                | _ -> null)
        let closure = if isNull field then target else field.GetValue target
        Sites<'T>.Get(builder, closure, file, line).Invoke closure
