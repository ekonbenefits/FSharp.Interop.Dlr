module Tests.Unreflected

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
#if DLR_CLOSURES
open Tests.ClosureBuilder
#endif

[<Fact>]
let ``dlr without ReflectedDefinition reports what is missing`` () =
    if Tests.Companion.bodiesMapped then raise (AnyUnit.IgnoreException "the companion's map holds this block's body (Tests/Companion.fs)")
    let w = box (Widget())
    // fsharpanalyzer: ignore-line-next DLR001
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> (dlr { return w?Count } : int) |> ignore)
    ex.Message |> should haveSubstring "[<ReflectedDefinition>] on the function or member that contains it"
    ex.Message |> should haveSubstring "not the whole module"
    ex.Message |> should not' (haveSubstring "could not decode")     // Undecodable.withVoid is elsewhere in this assembly

/// A refused block is refused once: later calls rethrow without translating again, which for a
/// missing attribute means without scanning the assembly again (#214). The first call raises the
/// refusal itself; each later call raises its own exception, with that refusal as its inner.
/// `clear` forgets it like any entry.
[<Fact>]
let ``a refused block is not translated again on every call`` () =
    let w = box (Widget())
    // fsharpanalyzer: ignore-line-next DLR001
    let call () = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> (dlr { return w?Count } : int) |> ignore)
    DlrCache.clear ()
    let first = call ()
    let before = DlrCache.count ()
    let second = call ()
    let third = call ()
    first.InnerException |> should equal null
    obj.ReferenceEquals(second.InnerException, first) |> should equal true
    obj.ReferenceEquals(third.InnerException, first) |> should equal true
    obj.ReferenceEquals(second, third) |> should equal false
    second.Message |> should equal first.Message
    DlrCache.count () |> should equal before
    DlrCache.clear ()
    let afterClear = call ()
    afterClear.InnerException |> should equal null
    obj.ReferenceEquals(afterClear, first) |> should equal false
    afterClear.Message |> should equal first.Message
