/// A reflected definition calling a function its signature hides (#213), in Tests.HiddenLib: in
/// Release the optimizer erases the function, FSharp.Core cannot decode any of that assembly's
/// reflected definitions, and every block in it fails. The analyzer reports the call at build
/// time (DLR008); this pins the run-time error behind it, and that it names the cause.
[<ReflectedDefinition>]
module Tests.Hidden

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open Tests.HiddenLib

#if DEBUG
[<Fact>]
let ``a function the signature hides is kept in Debug, so the assembly's blocks run`` () =
    Hidden.reflectedCaller (Widget()) 3 |> should equal 4
    Elsewhere.independent (Widget()) 3 |> should equal 6
#else
[<Fact>]
let ``a function the signature hides fails every block in its assembly, and the error says why`` () =
    for call in [ (fun () -> Hidden.reflectedCaller (Widget()) 3); (fun () -> Elsewhere.independent (Widget()) 3) ] do
        let ex = AnyUnit.Run.Assert.Current.Throws<FSharp.Interop.Dlr.DlrTranslationException>(fun () -> call () |> ignore)
        ex.Message |> should haveSubstring "could not decode"
        ex.Message |> should haveSubstring "Every reflected definition in Tests.HiddenLib fails the same way"
        ex.Message |> should haveSubstring "signature file"
        ex.Message |> should haveSubstring "DLR008"
        ex.Message |> should not' (haveSubstring "needs [<ReflectedDefinition>]")
#endif
