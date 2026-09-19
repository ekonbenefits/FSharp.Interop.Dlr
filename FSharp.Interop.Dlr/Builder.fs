namespace FSharp.Interop.Dlr

open System
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

[<Sealed>]
type DlrBuilder() =
    /// Both builders, this one first (the translator compiles a block of either kind nested in
    /// one of the other); sealed types, so the list is a constant, not a per-call closure.
    static let builderTypes = [ typeof<DlrBuilder>; typeof<DlrQuotedBuilder> ]
    member _.Return(value: 'T) = value
    member _.Zero() = ()
    member _.Delay(f: unit -> 'T) = f
    member _.Combine(first: unit, rest: unit -> 'T) : 'T = ignore first; rest ()
    member _.For(items: seq<'T>, body: 'T -> unit) : unit = Seq.iter body items
    member _.While(guard: unit -> bool, body: unit -> unit) : unit = while guard () do body ()
    member _.TryWith(body: unit -> 'T, handler: exn -> 'T) : 'T = try body () with e -> handler e
    member _.TryFinally(body: unit -> 'T, compensation: unit -> unit) : 'T = try body () finally compensation ()
    member _.Using(resource: 'R, body: 'R -> 'T) : 'T when 'R :> IDisposable = DlrRuntime.using resource body

    member _.Run(body: unit -> 'T,
                    [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
                    [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int) : 'T =
        Sites<'T>.Get(body, builderTypes, file, line).Invoke body

and [<Sealed>] DlrQuotedBuilder() =
    static let builderTypes = [ typeof<DlrQuotedBuilder>; typeof<DlrBuilder> ]
    member _.Return(value: 'T) = value
    member _.Zero() = ()
    member _.Delay(f: unit -> 'T) = f
    member _.Combine(first: unit, rest: unit -> 'T) : 'T = ignore first; rest ()
    member _.For(items: seq<'T>, body: 'T -> unit) : unit = Seq.iter body items
    member _.While(guard: unit -> bool, body: unit -> unit) : unit = while guard () do body ()
    member _.TryWith(body: unit -> 'T, handler: exn -> 'T) : 'T = try body () with e -> handler e
    member _.TryFinally(body: unit -> 'T, compensation: unit -> unit) : 'T = try body () finally compensation ()
    member _.Using(resource: 'R, body: 'R -> 'T) : 'T when 'R :> IDisposable = DlrRuntime.using resource body
    member _.Quote(q: Quotations.Expr<'T>) = q

    member _.Run(q: Quotations.Expr<unit -> 'T>,
                    [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
                    [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int) : 'T =
        QuotedSites<'T>.Get(builderTypes, q, file, line)

[<AutoOpen>]
module Builder =
    let dlr = DlrBuilder()
    let dlrq = DlrQuotedBuilder()
