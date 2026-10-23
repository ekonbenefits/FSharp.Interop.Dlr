/// A reflected definition FSharp.Core cannot decode. Not linked into Tests.Wasm: Mono's
/// interpreter asserts (reflection.c) on `typeof<System.Void>` in a stored quotation, taking the
/// whole process down, and the module's other members with it.
module Tests.Undecodable

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

// The attribute is there, but FSharp.Core refuses to decode a quotation holding `typeof<System.Void>`.
// The analyzer reports it at build time (DLR006); this pins the run-time error behind it.
[<ReflectedDefinition>]
let private withVoid (w: obj) : int =
    let t = typeof<System.Void>
    ignore t
    // fsharpanalyzer: ignore-line-next DLR006
    dlr { return w?Count }

/// A second one, in another type: FSharp.Core's message names no member, so the two fail alike,
/// and still only their own members do (the rest of this assembly decodes).
module Other =
    [<ReflectedDefinition>]
    let withVoidToo (w: obj) : int =
        let t = typeof<System.Void>
        ignore t
        // fsharpanalyzer: ignore-line-next DLR006
        dlr { return w?Count }

[<Fact>]
let ``a reflected definition FSharp.Core cannot decode is named, not blamed on a missing attribute`` () =
    let other = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> Other.withVoidToo (Widget()) |> ignore)
    other.Message |> should haveSubstring "withVoidToo"
    other.Message |> should not' (haveSubstring "every block in the assembly")
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> withVoid (Widget()) |> ignore)
    ex.Message |> should haveSubstring "withVoid"
    ex.Message |> should haveSubstring "could not decode"
    ex.Message |> should not' (haveSubstring "every block in the assembly")     // its own member only: the rest of this assembly decodes

/// A block in a local function of such a member: in Debug its closure is named after the local
/// (`inner@…`), not the member, so the member is named as the likely place rather than the error
/// resting on a missing attribute alone (#245). In Release the machine is named after the member.
[<ReflectedDefinition>]
let private voidLocal (w: obj) : int =
    let t = typeof<System.Void>
    ignore t
    // fsharpanalyzer: ignore-line-next DLR006
    let inner () : int = dlr { return w?Count }
    inner ()

[<Fact>]
let ``a block in a local function of a member FSharp.Core cannot decode names the member`` () =
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> voidLocal (Widget()) |> ignore)
    ex.Message |> should haveSubstring "voidLocal"
    ex.Message |> should haveSubstring "could not decode"
    ex.Message |> should not' (haveSubstring "trimmed")
    ex.Message.Split([| "Undecodable.voidLocal" |], System.StringSplitOptions.None).Length - 1 |> should equal 1   // named once

/// The same in a class's member: the class is nested in this module, and the closure too.
type VoidClass() =
    [<ReflectedDefinition>]
    member _.Count(w: obj) : int =
        let t = typeof<System.Void>
        ignore t
        // fsharpanalyzer: ignore-line-next DLR006
        let inner () : int = dlr { return w?Count }
        inner ()

[<Fact>]
let ``a block in a local function of a class member FSharp.Core cannot decode names the member`` () =
    let ex = AnyUnit.Run.Assert.Current.Throws<DlrTranslationException>(fun () -> VoidClass().Count(Widget()) |> ignore)
    ex.Message |> should haveSubstring "VoidClass.Count"
    ex.Message |> should haveSubstring "could not decode"
