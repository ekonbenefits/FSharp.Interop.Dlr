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

// The builder's members written out. With the body at the Run call this is a block like any
// other; with the Delay result bound or passed separately, the body is not where the call is,
// and the error says that rather than blaming a missing attribute. (The compiler also warns —
// FS3501 for the binding, FS3511 for the parameter — hence the nowarn; the analyzer sees the
// markers outside any Run, hence the ignores.)
#nowarn "3501"
#nowarn "3511"
[<ReflectedDefinition>]
module WrittenOut =
    let direct (w: obj) : int = dlr.Run(dlr.Delay(fun () -> dlr.Return (w?Count : int)))
    let viaLet (w: obj) : int =
        // fsharpanalyzer: ignore-line-next DLR002
        let code = dlr.Delay(fun () -> dlr.Return (w?Count : int))
        dlr.Run code
    let viaLetNoCapture () : int =
        let code = dlr.Delay(fun () -> dlr.Return 42)
        dlr.Run code
    let runCode (code: Microsoft.FSharp.Core.CompilerServices.ResumableCode<DlrData<int>, int>) : int = dlr.Run code
    // fsharpanalyzer: ignore-line-next DLR002
    let passedIn (w: obj) : int = runCode (dlr.Delay(fun () -> dlr.Return (w?Count : int)))

[<Fact>]
let ``the builder's members written out with the body at the Run call are a block`` () =
    WrittenOut.direct (box (Widget())) |> should equal 3

[<Fact>]
let ``Run applied to a value, not the body, says so`` () =
    let w = box (Widget())
    for f in [ (fun () -> WrittenOut.viaLet w); (fun () -> WrittenOut.viaLetNoCapture ()); (fun () -> WrittenOut.passedIn w) ] do
        let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> f () |> ignore)
        ex.Message |> should haveSubstring "Run is applied to a value"
        ex.Message |> should haveSubstring "dlr { … } at the call"
