namespace FSharp.Interop.Dlr

open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open Microsoft.FSharp.Core.CompilerServices

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
type DlrBuilder =
    new: unit -> DlrBuilder
    member inline Return: value: 'T -> ResumableCode<DlrData<'T>, 'T>
    member inline Zero: unit -> ResumableCode<'D, unit>
    /// <summary>Returns the body unevaluated, wrapped as resumable code; in the fallback path <c>Run</c> reads the closure back out of it.</summary>
    member inline Delay: [<InlineIfLambda>] delayed: (unit -> ResumableCode<'D, 'T>) -> ResumableCode<'D, 'T>
    member inline Combine: first: ResumableCode<'D, unit> * rest: ResumableCode<'D, 'T> -> ResumableCode<'D, 'T>
    /// <summary><c>for x in items do …</c>; the body's dynamic call sites are created once and reused across iterations.</summary>
    member inline For: items: seq<'X> * [<InlineIfLambda>] body: ('X -> ResumableCode<'D, unit>) -> ResumableCode<'D, unit>
    /// <summary><c>while guard do …</c>; a <c>let mutable</c>, in the block or captured, may be read and assigned by the guard and the body.</summary>
    member inline While: [<InlineIfLambda>] guard: (unit -> bool) * body: ResumableCode<'D, unit> -> ResumableCode<'D, unit>
    /// <summary><c>try … with</c>; a <c>RuntimeBinderException</c> from a failed dynamic bind can be caught here like any other.</summary>
    member inline TryWith: body: ResumableCode<'D, 'T> * [<InlineIfLambda>] handler: (exn -> ResumableCode<'D, 'T>) -> ResumableCode<'D, 'T>
    /// <summary><c>try … finally</c>.</summary>
    member inline TryFinally: body: ResumableCode<'D, 'T> * [<InlineIfLambda>] compensation: (unit -> unit) -> ResumableCode<'D, 'T>
    /// <summary><c>use x = …</c>; disposed when the block leaves the scope.</summary>
    member inline Using: resource: 'R * [<InlineIfLambda>] body: ('R -> ResumableCode<'D, 'T>) -> ResumableCode<'D, 'T> when 'R :> System.IDisposable
    /// <summary>Compiles (first call) and runs the block. <paramref name="file"/> and <paramref name="line"/> are filled in by the compiler and locate the body in the reflected definition.</summary>
    member inline Run:
        code: ResumableCode<DlrData<'T>, 'T> *
        [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string *
        [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int ->
            'T

[<AutoOpen>]
module Builder =
    /// <summary>The <c>dlr { }</c> computation expression. Put <c>[&lt;ReflectedDefinition&gt;]</c> on the function or member that contains the block (an enclosing type or module also works, but only when everything in it can be quoted).</summary>
    val dlr: DlrBuilder
