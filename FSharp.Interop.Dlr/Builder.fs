namespace FSharp.Interop.Dlr

// 3513: "resumable code invocation" at `code.Invoke(&sm)` in Run, expected when defining a
// resumable builder (FSharp.Core's task builder suppresses it the same way).
#nowarn "3513"

open System
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Core.CompilerServices.StateMachineHelpers

/// <summary>
/// Builder for <c>dlr { }</c>. The body is never executed as written: it is compiled as F#
/// resumable code into a struct state machine whose fields are the block's captured variables,
/// the block's quotation comes from the <c>[&lt;ReflectedDefinition&gt;]</c> on the function or
/// member containing it (or on an enclosing type or module), and it is compiled once into a
/// delegate whose DLR call sites are constants and whose input is that struct. Where the
/// compiler cannot build the state machine (Debug builds), the block's Delay closure plays
/// the same role.
/// </summary>
[<Sealed>]
type DlrBuilder() =
    /// <summary>Returns the body unevaluated, wrapped as resumable code; in the fallback path <c>Run</c> reads the closure back out of it.</summary>
    member inline _.Delay([<InlineIfLambda>] delayed: unit -> ResumableCode<'D, 'T>) : ResumableCode<'D, 'T> =
        ResumableCode<'D, 'T>(fun sm -> (delayed ()).Invoke(&sm))

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

    member inline this.Run(code: ResumableCode<DlrData<'T>, 'T>,
                           [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
                           [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int) : 'T =
        if __useResumableCode then
            // The compiler builds a struct per block: the captured variables are its fields, its
            // type is a JIT constant in Machine, and MoveNext (the body as written) is never called.
            __stateMachine<DlrData<'T>, 'T>
                (MoveNextMethodImpl<DlrData<'T>>(fun sm -> __resumeAt sm.ResumptionPoint; code.Invoke(&sm) |> ignore))
                (SetStateMachineMethodImpl<DlrData<'T>>(fun _ _ -> ()))
                (AfterCode<DlrData<'T>, 'T>(fun sm -> DlrRun.Machine(this, &sm, file, line)))
        else
            DlrRun.Closure(this, code, file, line)

[<AutoOpen>]
module Builder =
    let dlr = DlrBuilder()
