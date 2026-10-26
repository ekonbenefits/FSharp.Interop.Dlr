/// A type declared in a namespace, whose member FSharp.Core cannot decode: its closures are in the
/// file's <StartupCode$…> class, which nothing ties to the type (#245). Not linked into Tests.Wasm,
/// like Undecodable.fs: Mono asserts on the Void quotation.
namespace Tests.UndecodableTypes

open FSharp.Interop.Dlr
#if DLR_CLOSURES
open Tests.ClosureBuilder
#endif

type VoidInNamespace() =
    [<ReflectedDefinition>]
    member _.Count(w: obj) : int =
        let t = typeof<System.Void>
        ignore t
        // fsharpanalyzer: ignore-line-next DLR006
        let inner () : int = dlr { return w?Count }
        inner ()

/// A member with no attribute, named like VoidInNamespace's undecodable one: its block's closure
/// matches that member by name (`Count@…`), but it is not in it (#248).
type SameNamed() =
    // fsharpanalyzer: ignore-line-next DLR001
    member _.Count(w: obj) : int = dlr { return w?Count }

module Tests =
    open AnyUnit.Style.Xunit
    open AnyUnit.Style.FsUnit

    [<Fact>]
    let ``a same-named member of another type is not stated as the block's`` () =
        if global.Tests.Companion.bodiesMapped then raise (AnyUnit.IgnoreException "the companion's map holds this block's body (Tests/Companion.fs)")
        let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> SameNamed().Count(Tests.Widget()) |> ignore)
        ex.Message |> should haveSubstring "needs [<ReflectedDefinition>]"
        ex.Message |> should not' (haveSubstring "is in VoidInNamespace.Count")

    [<Fact>]
    let ``a block in a local function of a namespace type's member FSharp.Core cannot decode names the member`` () =
        let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> VoidInNamespace().Count(Tests.Widget()) |> ignore)
        ex.Message |> should haveSubstring "VoidInNamespace.Count"
        ex.Message |> should haveSubstring "could not decode"
