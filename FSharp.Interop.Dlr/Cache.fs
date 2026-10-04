namespace FSharp.Interop.Dlr

open System
open System.Diagnostics.CodeAnalysis
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
type internal Machines<'SM, 'T> [<ExcludeFromCodeCoverage>] private () =
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

/// The Delay closure out of the resumable-code delegate's target. The target is the builder's
/// own `Delay` lambda — one compiler-generated class per result type, holding the closure in a
/// field named `delayed` — so its reader is compiled once per `'T` (a LINQ field read; the
/// optimizer may instead inline the lambda away, and then the target is the closure itself and
/// the reader is the identity) and a call pays a type compare and a delegate invoke, not
/// reflection. The last reader is one immutable object swapped atomically; a target of another
/// type (not expected) goes to a dictionary, so alternating types cannot rebuild per call.
type private DelayedReader(targetType: Type, read: Func<obj, obj>) =
    member _.TargetType = targetType
    member _.Read = read

type internal Delayed<'T> [<ExcludeFromCodeCoverage>] private () =
    static let mutable last : DelayedReader = Unchecked.defaultof<_>
    static let readers = ConcurrentDictionary<Type, DelayedReader>()

    static let build (targetType: Type) =
        let read =
            match targetType.GetField("delayed", BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) with
            | f when not (isNull f) && f.FieldType = typeof<unit -> ResumableCode<DlrData<'T>, 'T>> ->
                let target = System.Linq.Expressions.Expression.Parameter(typeof<obj>, "target")
                System.Linq.Expressions.Expression.Lambda<Func<obj, obj>>(
                    System.Linq.Expressions.Expression.Field(System.Linq.Expressions.Expression.Convert(target, targetType), f), target).Compile()
            | _ when targetType.Assembly = typeof<Delayed<'T>>.Assembly ->
                // Our wrapper, but not the field we expect: the compiler's shape changed. Say so,
                // rather than treat the wrapper as the closure and key every block of this result
                // type on the one shared class.
                raise (DlrTranslationException(sprintf "dlr { } fallback path: the builder's Delay wrapper %s has no 'delayed' field of the expected type (the F# compiler's closure shape changed)." targetType.Name))
            | _ -> Func<obj, obj>(fun target -> target)
        DelayedReader(targetType, read)

    static member Of(target: obj) : obj =
        let t = target.GetType()
        let r = last
        let r =
            if not (obj.ReferenceEquals(r, null)) && obj.ReferenceEquals(r.TargetType, t) then r
            else
                let r = readers.GetOrAdd(t, build)
                last <- r
                r
        r.Read.Invoke target

/// One closure-path cache entry, immutable and generation-stamped like `MachineHit`.
[<AllowNullLiteral>]
type internal SiteHit<'T>(closureType: Type, f: Func<obj, 'T>, generation: int) =
    member _.ClosureType = closureType
    member _.Func = f
    member _.Generation = generation

/// The fallback path's cache, for blocks the compiler did not turn into a state machine (Debug
/// builds): per result type, the compiled `Func<obj, 'T>` by Delay-closure type, already typed,
/// so a call is one `GetType()`, one compare or lookup and one invoke.
type internal Sites<'T> [<ExcludeFromCodeCoverage>] private () =
    static let sites = ConcurrentDictionary<Type, SiteHit<'T>>()
    /// The last block *compiled* (an atomic reference to an immutable entry): a block called
    /// repeatedly pays a reference compare instead of a hash lookup. Installed only on a miss, so
    /// two blocks called in turn cost a lookup each and never write.
    static let mutable last : SiteHit<'T> = null
    static do lock DlrCache.onClear (fun () -> DlrCache.onClear.Add(fun () -> sites.Clear(); last <- null))

    static member Get(builder: obj, closure: obj, file: string, line: int) : Func<obj, 'T> =
        Sites<'T>.Get(builder, closure.GetType(), file, line)

    /// The closure class of a static resumable-code delegate (no target): the class its method
    /// lives in. `Delegate.Method` resolves a MethodInfo per call (~130 ns on a delegate the
    /// compiler allocates per call), so the last delegate seen is compared first — a static
    /// delegate's equality is its method pointer, so a re-entered block hits.
    static member val private lastCode : Delegate = null with get, set
    static member val private lastCodeType : Type = null with get, set
    static member ClosureTypeOf(code: Delegate) : Type =
        let last = Sites<'T>.lastCode
        if not (isNull last) && last.Equals code then Sites<'T>.lastCodeType
        else
            let t = code.Method.DeclaringType
            Sites<'T>.lastCode <- code
            Sites<'T>.lastCodeType <- t
            t

    /// By closure type: `closureType` is the block's Delay closure class, or for a capture-free
    /// block left with a static delegate (see `DlrRun.Closure`) the class its `Invoke` lives in.
    static member Get(builder: obj, closureType: Type, file: string, line: int) : Func<obj, 'T> =
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

    /// <summary>The fallback path (no statically compiled state machine, e.g. Debug): the block's Delay closure is read back out of the resumable-code delegate.</summary>
    static member Closure<'T>(builder: obj, code: ResumableCode<DlrData<'T>, 'T>, file: string, line: int) : 'T =
        let target = code.Target
        if isNull target then
            // An optimized build took the non-resumable path without a warning — a block whose
            // function-typed result is applied on the spot, `(dlr { … } : unit -> R) ()` — and,
            // the block capturing nothing, inlined its Delay closure into a static delegate whose
            // method lives in the closure class. Nothing to read at a call: compile from that
            // class and pass no closure.
            let closureType = Sites<'T>.ClosureTypeOf code
            if isNull closureType || closureType.Assembly = typeof<DlrRun>.Assembly then
                raise (DlrTranslationException(sprintf "dlr { } at %s:%d has no state machine and no closure to compile from (the builder's members called by hand?); write the block as dlr { … }." file line))
            Sites<'T>.Get(builder, closureType, file, line).Invoke null
        else
        let closure = Delayed<'T>.Of target
        Sites<'T>.Get(builder, closure, file, line).Invoke closure
