namespace FSharp.Interop.Dlr

open System
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

[<Sealed>]
type DlrBuilder() =
    member _.Return(value: 'T) = value
    member _.Zero() = ()
    member _.Delay(f: unit -> 'T) = f

    member this.Run(body: unit -> 'T,
                    [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
                    [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int) : 'T =
        let compiled = DlrCache.getOrCompile (this.GetType()) (body.GetType()) file line typeof<'T>
        (compiled.Delegate :?> Func<obj, 'T>).Invoke body

[<AutoOpen>]
module Builder =
    let dlr = DlrBuilder()
