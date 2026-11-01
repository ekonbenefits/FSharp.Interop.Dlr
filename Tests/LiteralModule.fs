/// A [<Literal>] in a [<ReflectedDefinition>] module (#284), in Tests.LiteralLib: FSharp.Core
/// cannot decode any of that assembly's reflected definitions, in Debug and Release, and every
/// block in it fails. FSharp.Core reads the whole table again on every lookup when it fails, so the
/// error must come after one read, not one per member: in an assembly this size, that looked like a
/// hang. The analyzer reports the literal at build time (DLR010); this pins the run-time error.
[<ReflectedDefinition>]
module Tests.LiteralModule

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open Tests.LiteralLib

[<Fact>]
let ``a literal in a reflected module fails every block in its assembly, and the error says why`` () =
    for call in [ (fun () -> Literal.Reflected.addHundred (Widget()) 3); (fun () -> Elsewhere.independent (Widget()) 3) ] do
        let ex = AnyUnit.Run.Assert.Current.Throws<FSharp.Interop.Dlr.DlrTranslationException>(fun () -> call () |> ignore)
        ex.Message |> should haveSubstring "could not decode any of Tests.LiteralLib's reflected definitions"
        ex.Message |> should haveSubstring "every block in the assembly fails"
        ex.Message |> should haveSubstring "Hundred"     // FSharp.Core's error names the literal: the culprit
        ex.Message |> should haveSubstring "[<Literal>]"
        ex.Message |> should not' (haveSubstring "needs [<ReflectedDefinition>]")
