namespace FSharp.Interop.Dlr

open System.Runtime.CompilerServices
open System.Runtime.InteropServices

/// <summary>
/// Builder for <c>dlr { }</c>. The body is never executed as written: its closure identifies the
/// block, the block's quotation comes from the enclosing <c>[&lt;ReflectedDefinition&gt;]</c>, and it is
/// compiled once into a delegate whose DLR call sites are constants and whose input is the closure.
/// </summary>
[<Sealed>]
type DlrBuilder =
    new: unit -> DlrBuilder
    member Return: value: 'T -> 'T
    member Zero: unit -> unit
    /// <summary>Returns the body closure unevaluated; <c>Run</c> uses it as the call-site key and value source.</summary>
    member Delay: f: (unit -> 'T) -> (unit -> 'T)
    member Combine: first: unit * rest: (unit -> 'T) -> 'T
    /// <summary><c>for x in items do …</c>; the body's dynamic call sites are created once and reused across iterations.</summary>
    member For: items: seq<'T> * body: ('T -> unit) -> unit
    /// <summary><c>while guard do …</c>; note F# does not let a <c>let mutable</c> be captured by the loop body, use a <c>ref</c>.</summary>
    member While: guard: (unit -> bool) * body: (unit -> unit) -> unit
    /// <summary><c>try … with</c>; a <c>RuntimeBinderException</c> from a failed dynamic bind can be caught here like any other.</summary>
    member TryWith: body: (unit -> 'T) * handler: (exn -> 'T) -> 'T
    /// <summary><c>try … finally</c>.</summary>
    member TryFinally: body: (unit -> 'T) * compensation: (unit -> unit) -> 'T
    /// <summary><c>use x = …</c>; disposed when the block leaves the scope.</summary>
    member Using: resource: 'R * body: ('R -> 'T) -> 'T when 'R :> System.IDisposable
    /// <summary>Compiles (first call) and runs the block. <paramref name="file"/> and <paramref name="line"/> are filled in by the compiler and locate the body in the reflected definition.</summary>
    member Run:
        body: (unit -> 'T) *
        [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string *
        [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int ->
            'T

[<AutoOpen>]
module Builder =
    /// <summary>The <c>dlr { }</c> computation expression. The enclosing module, type or member must be <c>[&lt;ReflectedDefinition&gt;]</c>.</summary>
    val dlr: DlrBuilder
