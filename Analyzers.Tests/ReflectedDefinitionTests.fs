module Analyzers.Tests.ReflectedDefinition

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Analyzers.SDK
open FSharp.Analyzers.SDK.Testing
open FSharp.Interop.Dlr.Analyzers

// A stand-in with the builder's full name, so the test source type-checks with no package feed;
// the analyzer identifies the block by FSharp.Interop.Dlr.DlrBuilder.Run.
let private prelude = """
namespace FSharp.Interop.Dlr
type DlrBuilder() =
    member _.Return(x: 'T) = x
    member _.Delay(f: unit -> 'T) = f
    member _.Run(f: unit -> 'T) : 'T = f ()
[<AutoOpen>]
module Builder =
    let dlr = DlrBuilder()

namespace Demo
open FSharp.Interop.Dlr
"""

let private options = lazy (mkOptionsFromProject "net10.0" [] |> Async.AwaitTask |> Async.RunSynchronously)

let private run (source: string) : Message list =
    let ctx = getContext options.Value (prelude + source)
    ReflectedDefinitionAnalyzer.cliAnalyzer ctx |> Async.RunSynchronously

[<Fact>]
let ``a function without the attribute is reported with a fix on its let`` () =
    let msgs =
        run """
module Impl =
    let w = box 1
    let count () : int = dlr { return 3 }
"""
    msgs.Length |> should equal 1
    msgs.[0].Code |> should equal ReflectedDefinitionAnalyzer.Code
    msgs.[0].Severity |> should equal Severity.Error
    Assert.messageContains "'count'" msgs.[0] |> should equal true
    msgs.[0].Fixes.Length |> should equal 1
    msgs.[0].Fixes.[0].ToText |> should equal "[<ReflectedDefinition>]\n    "
    msgs.[0].Fixes.[0].FromRange.StartColumn |> should equal 4

[<Fact>]
let ``the attribute on the function satisfies it`` () =
    run """
module Impl =
    [<ReflectedDefinition>]
    let count () : int = dlr { return 3 }
""" |> should be Empty

[<Fact>]
let ``the attribute on the module or the type satisfies it`` () =
    run """
[<ReflectedDefinition>]
module Impl =
    let count () : int = dlr { return 3 }
""" |> should be Empty
    run """
[<ReflectedDefinition>]
type Holder(w: obj) =
    member _.Count: int = dlr { return 3 }
""" |> should be Empty

[<Fact>]
let ``a member without the attribute is reported with an indented fix`` () =
    let msgs =
        run """
type Holder(w: obj) =
    member _.Count: int = dlr { return 3 }
"""
    msgs.Length |> should equal 1
    Assert.messageContains "'Count'" msgs.[0] |> should equal true
    msgs.[0].Fixes.[0].ToText |> should equal "[<ReflectedDefinition>]\n    "
    msgs.[0].Fixes.[0].FromRange.StartColumn |> should equal 4

[<Fact>]
let ``a block in a nested local function points the fix at the outer binding`` () =
    let msgs =
        run """
module Impl =
    let outer (w: obj) =
        let inner () : int = dlr { return 3 }
        inner ()
"""
    msgs.Length |> should equal 1
    Assert.messageContains "'outer'" msgs.[0] |> should equal true
    // The innermost *syntax* binding is `inner`; the attribute has to go on the member the
    // compiler stores, which is `outer`, so the fix targets the outermost binding's keyword.
    msgs.[0].Fixes.[0].FromRange.StartLine |> should equal (msgs.[0].Range.StartLine - 1)

[<Fact>]
let ``module-level code is reported without a fix`` () =
    let msgs =
        run """
module Impl =
    let w = box 1
    do (dlr { return 3 } : int) |> ignore
"""
    msgs.Length |> should equal 1
    Assert.messageContains "move the block into a function" msgs.[0] |> should equal true

[<Fact>]
let ``module-level code is reported even under a module attribute`` () =
    // The static initializer has no reflected definition, so the attribute cannot help there.
    let msgs =
        run """
[<ReflectedDefinition>]
module Impl =
    let w = box 1
    do (dlr { return 3 } : int) |> ignore
"""
    msgs.Length |> should equal 1
    msgs.[0].Fixes |> should be Empty

[<Fact>]
let ``two blocks give two reports`` () =
    let msgs =
        run """
module Impl =
    let a () : int = dlr { return 1 }
    let b () : int = dlr { return 2 }
"""
    msgs.Length |> should equal 2
