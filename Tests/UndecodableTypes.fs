/// A type declared in a namespace, whose member FSharp.Core cannot decode: its closures are in the
/// file's <StartupCode$…> class, which nothing ties to the type (#245). Not linked into Tests.Wasm,
/// like Undecodable.fs: Mono asserts on the Void quotation.
namespace Tests.UndecodableTypes

open FSharp.Interop.Dlr

type VoidInNamespace() =
    [<ReflectedDefinition>]
    member _.Count(w: obj) : int =
        let t = typeof<System.Void>
        ignore t
        // fsharpanalyzer: ignore-line-next DLR006
        let inner () : int = dlr { return w?Count }
        inner ()

module Tests =
    open AnyUnit.Style.Xunit
    open AnyUnit.Style.FsUnit

    [<Fact>]
    let ``a block in a local function of a namespace type's member FSharp.Core cannot decode names the member`` () =
        let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> VoidInNamespace().Count(Tests.Widget()) |> ignore)
        ex.Message |> should haveSubstring "VoidInNamespace.Count"
        ex.Message |> should haveSubstring "could not decode"
