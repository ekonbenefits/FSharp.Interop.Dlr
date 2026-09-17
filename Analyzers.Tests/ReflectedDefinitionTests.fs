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
[<AutoOpen>]
module Operators =
    let ( ? ) (target: obj) (name: string) : 'T = failwith "marker"
    let ( ?<- ) (target: obj) (name: string) (value: 'V) : unit = failwith "marker"
    let ( ?+? ) (left: obj) (right: obj) : 'T = failwith "marker"
type Named<'T> private () = class end
type Indexed<'T> private () =
    member _.Item with get (i: obj) : 'T = failwith "marker" and set (i: obj) (v: 'T) = failwith "marker"
[<Sealed; AbstractClass>]
type Dlr =
    static member get (name: string) (target: obj) : 'T = failwith "marker"
    static member named (record: 'T) : Named<'T> = failwith "marker"
    static member idx (target: obj) : Indexed<'T> = failwith "marker"

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

let private outside (msgs: Message list) = msgs |> List.filter (fun m -> m.Code = ReflectedDefinitionAnalyzer.OutsideCode)

[<Fact>]
let ``markers used outside a block are reported`` () =
    let msgs =
        run """
module Impl =
    let w = box 1
    let a : int = w?Count
    let b () = w?Count <- 1
    let c : int = w ?+? (box 2)
    let d : int = w |> Dlr.get "Count"
    let e : int = (Dlr.idx w).[0]
"""
        |> outside
    msgs.Length |> should equal 5
    msgs |> List.forall (fun m -> m.Severity = Severity.Error) |> should equal true
    msgs.[0].Message |> should haveSubstring "only meaningful inside dlr { }"

[<Fact>]
let ``markers inside a block, including inside a lambda in the block, are fine`` () =
    run """
module Impl =
    let w = box 1
    [<ReflectedDefinition>]
    let a () : int = dlr { return w?Count }
    [<ReflectedDefinition>]
    let b () : int list = dlr { return [ 1; 2 ] |> List.map (fun i -> (w?Add(i) : int)) }
    [<ReflectedDefinition>]
    let c () : int = dlr { return w |> Dlr.get "Count" }
"""
    |> outside
    |> should be Empty

[<Fact>]
let ``a marker outside a block in the same function as a block is still reported`` () =
    let msgs =
        run """
module Impl =
    let w = box 1
    [<ReflectedDefinition>]
    let f () : int =
        let n: int = dlr { return w?Count }
        n + w?Count
"""
        |> outside
    msgs.Length |> should equal 1
