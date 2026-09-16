namespace FSharp.Interop.Dlr

open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open FSharp.Quotations

/// <summary>
/// Builder for <c>dlr { }</c>. The body is captured as a quotation, compiled once per source
/// location into a delegate whose DLR call sites are constants, and then invoked with the
/// current closure values.
/// </summary>
[<Sealed>]
type DlrBuilder =
    new: unit -> DlrBuilder
    member Return: value: 'T -> 'T
    member Zero: unit -> unit
    member Delay: f: (unit -> 'T) -> 'T
    member Quote: quotation: Expr<'T> -> Expr<'T>
    /// <summary>Compiles (first call) and runs the quoted body. <paramref name="file"/> and <paramref name="line"/> are filled in by the compiler and key the cache.</summary>
    member Run:
        quotation: Expr<'T> *
        [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string *
        [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int ->
            'T

[<AutoOpen>]
module Builder =
    /// <summary>The <c>dlr { }</c> computation expression.</summary>
    val dlr: DlrBuilder
