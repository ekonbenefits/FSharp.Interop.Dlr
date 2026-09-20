/// A reflected definition FSharp.Core cannot decode. Not linked into Tests.Wasm: Mono's
/// interpreter asserts (reflection.c) on `typeof<System.Void>` in a stored quotation, taking the
/// whole process down, and the module's other members with it.
module Tests.Undecodable

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

// The attribute is there, but FSharp.Core refuses to decode a quotation holding `typeof<System.Void>`.
[<ReflectedDefinition>]
let private withVoid (w: obj) : int =
    let t = typeof<System.Void>
    ignore t
    dlr { return w?Count }

[<Fact>]
let ``a reflected definition FSharp.Core cannot decode is named, not blamed on a missing attribute`` () =
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> withVoid (Widget()) |> ignore)
    ex.Message |> should haveSubstring "withVoid"
    ex.Message |> should haveSubstring "could not be decoded"
