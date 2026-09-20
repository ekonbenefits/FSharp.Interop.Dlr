module Tests.Unreflected

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

[<Fact>]
let ``dlr without ReflectedDefinition reports what is missing`` () =
    let w = box (Widget())
    // fsharpanalyzer: ignore-line-next DLR001
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> (dlr { return w?Count } : int) |> ignore)
    ex.Message |> should haveSubstring "[<ReflectedDefinition>] on the function or member that contains it"
    ex.Message |> should haveSubstring "not the whole module"
    ex.Message |> should not' (haveSubstring "could not be decoded")     // Undecodable.withVoid is elsewhere in this assembly
