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

/// A refused block is refused once: later calls rethrow without translating again, which for a
/// missing attribute means without scanning the assembly again (#214). Each call raises its own
/// exception, with the one refusal as its inner; `clear` forgets it like any entry.
[<Fact>]
let ``a refused block is not translated again on every call`` () =
    let w = box (Widget())
    // fsharpanalyzer: ignore-line-next DLR001
    let call () = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> (dlr { return w?Count } : int) |> ignore)
    let first = call ()
    let before = DlrCache.count ()
    let second = call ()
    obj.ReferenceEquals(first, second) |> should equal false
    first.InnerException |> should be instanceOfType<DlrTranslationException>
    obj.ReferenceEquals(first.InnerException, second.InnerException) |> should equal true
    second.Message |> should equal first.Message
    DlrCache.count () |> should equal before
    DlrCache.clear ()
    let afterClear = call ()
    obj.ReferenceEquals(afterClear.InnerException, first.InnerException) |> should equal false
    afterClear.Message |> should equal first.Message
