namespace FSharp.Interop.Dlr

open System.Runtime.CompilerServices
open System.Runtime.InteropServices

/// <summary>
/// Builder for <c>dlr { }</c>. The body is never executed as written: its closure identifies the
/// block, the block's quotation comes from the <c>[&lt;ReflectedDefinition&gt;]</c> on the function or
/// member containing it (or on an enclosing type or module), and it is compiled once into a
/// delegate whose DLR call sites are constants and whose input is the closure.
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
    /// <summary><c>while guard do …</c>; a <c>let mutable</c>, in the block or captured, may be read and assigned by the guard and the body.</summary>
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

/// <summary>
/// Builder for <c>dlrq { }</c>: the same blocks as <c>dlr { }</c>, with the body quoted by the
/// compiler at the block itself, so nothing around it needs <c>[&lt;ReflectedDefinition&gt;]</c>. F#
/// materialises that quotation on every call, which costs microseconds where <c>dlr { }</c>
/// costs nanoseconds; the compiled delegate is cached the same way.
/// </summary>
and [<Sealed>] DlrQuotedBuilder =
    new: unit -> DlrQuotedBuilder
    member Return: value: 'T -> 'T
    member Zero: unit -> unit
    member Delay: f: (unit -> 'T) -> (unit -> 'T)
    member Combine: first: unit * rest: (unit -> 'T) -> 'T
    member For: items: seq<'T> * body: ('T -> unit) -> unit
    member While: guard: (unit -> bool) * body: (unit -> unit) -> unit
    member TryWith: body: (unit -> 'T) * handler: (exn -> 'T) -> 'T
    member TryFinally: body: (unit -> 'T) * compensation: (unit -> unit) -> 'T
    member Using: resource: 'R * body: ('R -> 'T) -> 'T when 'R :> System.IDisposable
    /// <summary>Makes the compiler pass the block as a quotation.</summary>
    member Quote: q: Quotations.Expr<'T> -> Quotations.Expr<'T>
    /// <summary>Compiles (first call at this site, per shape) and runs the block; the captured values are read out of the quotation on every call.</summary>
    member Run:
        q: Quotations.Expr<unit -> 'T> *
        [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string *
        [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int ->
            'T

[<AutoOpen>]
module Builder =
    /// <summary>The <c>dlr { }</c> computation expression. Put <c>[&lt;ReflectedDefinition&gt;]</c> on the function or member that contains the block (an enclosing type or module also works, but only when everything in it can be quoted).</summary>
    val dlr: DlrBuilder
    /// <summary>The <c>dlrq { }</c> computation expression: <c>dlr { }</c> without the attribute, at a per-call cost of microseconds rather than nanoseconds. Same body, same bindings; only public members bind, and a captured <c>let mutable</c> can be read but not assigned.</summary>
    val dlrq: DlrQuotedBuilder
