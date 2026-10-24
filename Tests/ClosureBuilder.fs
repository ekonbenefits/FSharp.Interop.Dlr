/// `dlr { }` on the closure path in every build (#180): `DlrBuilder` without its
/// `if __useResumableCode` branch, so `Run` always goes to `DlrRun.Closure` with the same Delay
/// wrapper, and the suite exercises `Delayed<'T>` and `Sites<'T>` in Debug and Release. That is
/// the path a Debug build on SDK 10.0.403 and older takes, and the one shape Release declines;
/// a Debug build on the 11.0 compiler no longer takes it at all. Compiled in every build so it
/// cannot drift from `DlrBuilder`; the test files open it only under `-p:DlrClosures=true`.
module Tests.ClosureBuilder

// 3513: "resumable code invocation" at `(delayed ()).Invoke(&sm)`, as in DlrBuilder.
#nowarn "3513"

open System
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open Microsoft.FSharp.Core.CompilerServices
open FSharp.Interop.Dlr

[<Sealed>]
type DlrClosureBuilder() =
    /// As DlrBuilder's: the caller's file and line answer the probe on a cache miss.
    member inline _.Delay([<InlineIfLambda>] delayed: unit -> ResumableCode<'D, 'T>,
                          [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
                          [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int) : ResumableCode<'D, 'T> =
        ResumableCode<'D, 'T>(fun sm ->
            if DlrLocation.Probing then
                DlrLocation.Answer(file, line)
                true
            else (delayed ()).Invoke(&sm))

    member inline _.Return(value: 'T) : ResumableCode<DlrData<'T>, 'T> =
        ResumableCode<DlrData<'T>, 'T>(fun sm -> sm.Data.Result <- value; true)

    member inline _.Zero() : ResumableCode<'D, unit> = ResumableCode.Zero()

    member inline _.Combine(first: ResumableCode<'D, unit>, rest: ResumableCode<'D, 'T>) : ResumableCode<'D, 'T> =
        ResumableCode.Combine(first, rest)

    member inline _.For(items: seq<'X>, [<InlineIfLambda>] body: 'X -> ResumableCode<'D, unit>) : ResumableCode<'D, unit> =
        ResumableCode.For(items, body)

    member inline _.While([<InlineIfLambda>] guard: unit -> bool, body: ResumableCode<'D, unit>) : ResumableCode<'D, unit> =
        ResumableCode.While(guard, body)

    member inline _.TryWith(body: ResumableCode<'D, 'T>, [<InlineIfLambda>] handler: exn -> ResumableCode<'D, 'T>) : ResumableCode<'D, 'T> =
        ResumableCode.TryWith(body, handler)

    member inline _.TryFinally(body: ResumableCode<'D, 'T>, [<InlineIfLambda>] compensation: unit -> unit) : ResumableCode<'D, 'T> =
        ResumableCode.TryFinally(body, ResumableCode<'D, unit>(fun _ -> compensation (); true))

    member inline _.Using(resource: 'R, [<InlineIfLambda>] body: 'R -> ResumableCode<'D, 'T>) : ResumableCode<'D, 'T> when 'R :> IDisposable =
        ResumableCode.Using(resource, body)

    member inline this.Run(code: ResumableCode<DlrData<'T>, 'T>) : 'T = DlrRun.Closure(this, code)

/// Opened after FSharp.Interop.Dlr in each test file under DLR_CLOSURES, so it shadows `dlr`.
let dlr = DlrClosureBuilder()
