namespace FSharp.Interop.Dlr

// 3513: "resumable code invocation" at `code.Invoke(&sm)` in Locate.Code, the fallback path's
// probe: it runs the block's outermost Delay, which answers without running the body.
#nowarn "3513"

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

    /// Blocks the translator refused, by the same key: a refusal is deterministic for a compiled
    /// block, so retrying it per call (the whole translation, and for a missing attribute an
    /// assembly scan) cannot succeed (#214). Not counted in `count`. Only the translator's own
    /// refusals, which carry no inner exception: a failure it wraps (compiling the tree ran out of
    /// stack or memory, an assembly not yet resolvable) may not recur, so it is retried.
    let private refused = ConcurrentDictionary<Type, DlrTranslationException>()

    /// A later call at a refused block raises its own exception (one instance thrown from several
    /// threads would share its stack trace), with the refusal and its stack as the inner.
    let private refusedAgain (e: DlrTranslationException) = raise (DlrTranslationException(e.Message, e))

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
        refused.Clear()
        Interlocked.Increment &generation |> ignore
        lock onClear (fun () -> for f in onClear do f ())

    let internal getOrCompile (builderType: unit -> Type) (blockType: Type) (locate: unit -> struct (string * int)) (resultType: Type) =
        match cache.TryGetValue blockType with
        | true, compiled -> compiled
        | _ ->
        match refused.TryGetValue blockType with
        | true, e -> refusedAgain e
        | _ ->
        try
            cache.GetOrAdd(blockType, fun t ->
                let builderType = builderType ()
                let struct (file, line) = locate ()
                let found = Discover.findBody builderType t file line
                // The compiled block's name, which a stack trace prints for its frame: the block has
                // no line numbers of its own, so this says which block it is. F#'s closure-name shape,
                // `name@line` (`run@7`), with the file in the name part since the block has no debug
                // info to supply it: `dlr__Program_fs@7`. Letters, digits and `_` only before the `@`:
                // a trace parser splits a frame at its last `.`, and a `:` or space trips others.
                // The file is cut at either separator: the path is recorded where the caller was built,
                // so a Windows path can reach a Linux run, where GetFileName keeps it whole.
                let identifier (s: string) = String(s |> Seq.map (fun c -> if Char.IsLetterOrDigit c then c else '_') |> Array.ofSeq)
                let name =
                    if String.IsNullOrEmpty file then "dlr__" + identifier t.Name
                    else sprintf "dlr__%s@%d" (identifier (file.Substring(file.LastIndexOfAny [| '/'; '\\' |] + 1))) line
                Translate.translate name builderType found.Context found.MemberBody t resultType found.Body)
        with :? DlrTranslationException as e when isNull e.InnerException ->
            refused.TryAdd(blockType, e) |> ignore
            reraise ()

/// A block's file and line, which locate its body in the reflected definition: its outermost
/// Delay answers a probe with its caller info (see `DlrLocation`). Run on a miss only.
type internal Locate private () =
    static member private Checked(struct (file: string, line: int)) =
        if line < 0 then
            raise (DlrTranslationException "dlr { } could not be located: its code did not start with the builder's Delay (the builder's members called by hand?); write the block as dlr { … }.")
        struct (file, line)

    /// From a copy of the state machine: its MoveNext is the block's code.
    static member Machine<'SM>(sm: 'SM) : struct (string * int) =
        let copy = box sm :?> System.Runtime.CompilerServices.IAsyncStateMachine
        Locate.Checked(DlrLocation.Probe(fun () -> copy.MoveNext()))

    /// From the resumable code the fallback path is handed.
    static member Code<'T>(code: ResumableCode<DlrData<'T>, 'T>) : struct (string * int) =
        Locate.Checked(DlrLocation.Probe(fun () ->
            let mutable sm = ResumableStateMachine<DlrData<'T>>()
            code.Invoke(&sm) |> ignore))

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

    static member Get(builder: obj, sm: inref<'SM>) : DlrReader<'SM, 'T> =
        let hit = Machines<'SM, 'T>.hit
        if not (isNull hit) && hit.Generation = DlrCache.generation then hit.Reader
        else Machines<'SM, 'T>.Miss(builder, sm)

    static member private Miss(builder: obj, sm: 'SM) : DlrReader<'SM, 'T> =
        let generation = Volatile.Read &DlrCache.generation
        let compiled = DlrCache.getOrCompile (fun () -> builder.GetType()) typeof<'SM> (fun () -> Locate.Machine<'SM> sm) typeof<'T>
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

/// The last static resumable-code delegate seen and its closure class, as one immutable pair (#175).
[<AllowNullLiteral>]
type internal CodeHit(code: Delegate, closureType: Type) =
    member _.Code = code
    member _.ClosureType = closureType

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

    static member Get(builder: obj, closure: obj, code: ResumableCode<DlrData<'T>, 'T>) : Func<obj, 'T> =
        Sites<'T>.Get(builder, closure.GetType(), code)

    /// The closure class of a static resumable-code delegate (no target): the class its method
    /// lives in. `Delegate.Method` resolves a MethodInfo per call (~130 ns on a delegate the
    /// compiler allocates per call), so the last delegate seen is compared first — a static
    /// delegate's equality is its method pointer, so a re-entered block hits. The delegate and its
    /// class are one immutable pair swapped atomically: two fields could be read torn (#175).
    static member val private lastCode : CodeHit = null with get, set
    static member ClosureTypeOf(code: Delegate) : Type =
        let last = Sites<'T>.lastCode
        if not (isNull last) && last.Code.Equals code then last.ClosureType
        else
            let t = code.Method.DeclaringType
            Sites<'T>.lastCode <- CodeHit(code, t)
            t

    /// By closure type: `closureType` is the block's Delay closure class, or for a capture-free
    /// block left with a static delegate (see `DlrRun.Closure`) the class its `Invoke` lives in.
    static member Get(builder: obj, closureType: Type, code: ResumableCode<DlrData<'T>, 'T>) : Func<obj, 'T> =
        let generation = Volatile.Read &DlrCache.generation
        let hit = last
        if not (isNull hit) && obj.ReferenceEquals(hit.ClosureType, closureType) && hit.Generation = generation then hit.Func
        else
            match sites.TryGetValue closureType with
            | true, entry when entry.Generation = generation -> entry.Func
            | _ ->
                let compiled = DlrCache.getOrCompile (fun () -> builder.GetType()) closureType (fun () -> Locate.Code code) typeof<'T>
                let entry = SiteHit<'T>(closureType, compiled.Delegate :?> Func<obj, 'T>, generation)
                sites.[closureType] <- entry
                last <- entry
                entry.Func

/// Why a trimmed or NativeAOT publish breaks every block: the reason the Requires* attributes on
/// `DlrRun` give at the user's `dlr { }` line.
module internal Unsupported =
    [<Literal>]
    let JitOnly =
        "dlr { } needs the JIT and untrimmed metadata: it reads the block's [<ReflectedDefinition>] quotation, binds through the C# runtime binder and compiles LINQ expressions at run time. Trimming and NativeAOT are not supported."

/// <summary>The entry points the inlined <c>Run</c> compiles to. Not for direct use.</summary>
/// <remarks>Not part of the supported API: public only because compiled blocks call it, and it may change in any release.</remarks>
[<Sealed; AbstractClass>]
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
type DlrRun =

    /// <summary>The state machine path: <typeparamref name="SM"/> is the block's struct, a JIT constant here.</summary>
#if NET
    [<RequiresUnreferencedCode(Unsupported.JitOnly); RequiresDynamicCode(Unsupported.JitOnly)>]
#endif
    static member Machine<'SM, 'T>(builder: obj, sm: byref<'SM>) : 'T =
        Machines<'SM, 'T>.Get(builder, &sm).Invoke(&sm)

    /// <summary>The fallback path (no statically compiled state machine, e.g. Debug): the block's Delay closure is read back out of the resumable-code delegate.</summary>
#if NET
    [<RequiresUnreferencedCode(Unsupported.JitOnly); RequiresDynamicCode(Unsupported.JitOnly)>]
#endif
    static member Closure<'T>(builder: obj, code: ResumableCode<DlrData<'T>, 'T>) : 'T =
        let target = code.Target
        if isNull target then
            // An optimized build took the non-resumable path without a warning — a block whose
            // function-typed result is applied on the spot, `(dlr { … } : unit -> R) ()` — and,
            // the block capturing nothing, inlined its Delay closure into a static delegate whose
            // method lives in the closure class. Nothing to read at a call: compile from that
            // class and pass no closure.
            let closureType = Sites<'T>.ClosureTypeOf code
            if isNull closureType || closureType.Assembly = typeof<DlrRun>.Assembly then
                let struct (file, line) = Locate.Code code
                raise (DlrTranslationException(sprintf "dlr { } at %s:%d has no state machine and no closure to compile from (the builder's members called by hand?); write the block as dlr { … }." file line))
            Sites<'T>.Get(builder, closureType, code).Invoke null
        else
        let closure = Delayed<'T>.Of target
        Sites<'T>.Get(builder, closure, code).Invoke closure
