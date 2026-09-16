namespace FSharp.Interop.Dlr

open System
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open FSharp.Quotations

[<Sealed>]
type DlrBuilder() =
    member _.Return(value: 'T) = value
    member _.Zero() = ()
    member _.Delay(f: unit -> 'T) = f ()
    member _.Quote(quotation: Expr<'T>) = quotation

    member this.Run(quotation: Expr<'T>,
                    [<CallerFilePath; Optional; DefaultParameterValue("")>] file: string,
                    [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int) : 'T =
        let compiled = DlrCache.getOrCompile (this.GetType()) file line typeof<'T> quotation
        let slots = Translate.extractSlots quotation
        (compiled.Delegate :?> Func<obj[], 'T>).Invoke slots

[<AutoOpen>]
module Builder =
    let dlr = DlrBuilder()
