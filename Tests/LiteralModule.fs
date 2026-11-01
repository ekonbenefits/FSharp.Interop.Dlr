/// A [<Literal>] in a [<ReflectedDefinition>] module (#284), in Tests.LiteralLib: FSharp.Core
/// cannot decode any of that assembly's reflected definitions, in Debug and Release, and every
/// block in it fails. FSharp.Core reads the whole table again on every lookup when it fails, so the
/// error must come after one read, not one per member: in an assembly this size, that looked like a
/// hang. The analyzer reports the literal at build time (DLR010); this pins the run-time error.
[<ReflectedDefinition>]
module Tests.LiteralModule

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open Tests.LiteralLib

[<Fact>]
let ``a literal in a reflected module fails every block in its assembly, and the error says why`` () =
    // Each read of the failing table throws FSharp.Core's error once: counted, two blocks read it
    // once between them (the second finds it cached), not once per member of the assembly.
    // (One exception per read, raised again on its way out: counted once; the errors quoting it are not reads.)
    let reads = Collections.Generic.HashSet<exn>(HashIdentity.Reference)
    let counter = EventHandler<Runtime.ExceptionServices.FirstChanceExceptionEventArgs>(fun _ args ->
        if not (args.Exception :? FSharp.Interop.Dlr.DlrTranslationException) && args.Exception.Message.Contains "Could not bind property Hundred" then reads.Add args.Exception |> ignore)
    AppDomain.CurrentDomain.FirstChanceException.AddHandler counter
    try
        for call in [ (fun () -> Literal.Reflected.addHundred (Widget()) 3); (fun () -> Elsewhere.independent (Widget()) 3) ] do
            let ex = AnyUnit.Run.Assert.Current.Throws<FSharp.Interop.Dlr.DlrTranslationException>(fun () -> call () |> ignore)
            ex.Message |> should haveSubstring "could not decode any of Tests.LiteralLib's reflected definitions"
            ex.Message |> should haveSubstring "every block in the assembly fails"
            ex.Message |> should haveSubstring "Hundred"     // FSharp.Core's error names the literal: the culprit
            ex.Message |> should haveSubstring "[<Literal>]"
            ex.Message |> should not' (haveSubstring "needs [<ReflectedDefinition>]")
    finally
        AppDomain.CurrentDomain.FirstChanceException.RemoveHandler counter
    reads.Count |> should equal 1
