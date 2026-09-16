namespace FSharp.Interop.Dlr

open System
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

[<Sealed>]
type DlrBuilder() =
    member _.Return(value: 'T) = value
    member _.Zero() = ()
    member _.Delay(f: unit -> 'T) = f
    member _.Combine(first: unit, rest: unit -> 'T) : 'T = ignore first; rest ()
    member _.For(items: seq<'T>, body: 'T -> unit) : unit = Seq.iter body items
    member _.While(guard: unit -> bool, body: unit -> unit) : unit = while guard () do body ()
    member _.TryWith(body: unit -> 'T, handler: exn -> 'T) : 'T = try body () with e -> handler e
    member _.TryFinally(body: unit -> 'T, compensation: unit -> unit) : 'T = try body () finally compensation ()

    member this.Run(body: unit -> 'T,
                    [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
                    [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int) : 'T =
        let compiled = DlrCache.getOrCompile (this.GetType()) (body.GetType()) file line typeof<'T>
        (compiled.Delegate :?> Func<obj, 'T>).Invoke body

[<AutoOpen>]
module Builder =
    let dlr = DlrBuilder()
