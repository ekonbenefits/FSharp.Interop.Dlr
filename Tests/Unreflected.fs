module Tests.Unreflected

open Xunit
open FsUnit.Xunit
open FSharp.Interop.Dlr

[<Fact>]
let ``dlr without ReflectedDefinition reports what is missing`` () =
    let w = box (Widget())
    let ex = Assert.Throws<DlrTranslationException>(fun () -> (dlr { return w?Count } : int) |> ignore)
    ex.Message |> should haveSubstring "ReflectedDefinition"
