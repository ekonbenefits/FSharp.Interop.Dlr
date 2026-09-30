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
type TypeArgs private () = class end
[<Sealed; AbstractClass>]
type Dlr =
    static member get (name: string) (target: obj) : 'T = failwith "marker"
    static member named (record: 'T) : Named<'T> = failwith "marker"
    static member namedOf (args: (string * obj) list) : Named<(string * obj) list> = failwith "marker"
    static member argsOf (args: obj list) : Named<obj list> = failwith "marker"
    static member typeArgs<'A> () : TypeArgs = failwith "marker"
    static member typeArgsOf (types: System.Type list) : TypeArgs = failwith "marker"
    static member item (indexes: 'TIndexes) (target: obj) : 'T = failwith "marker"
    static member invoke (name: string) (args: 'TArgs) (target: obj) : 'T = failwith "marker"
    static member call (target: obj) : 'T = failwith "marker"
    static member apply (args: 'TArgs) (target: obj) : 'T = failwith "marker"
    static member new'<'T> (a: obj) : 'T = failwith "marker"
    static member new'<'T> (a: obj, b: obj) : 'T = failwith "marker"
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Dlr =
    [<Sealed; AbstractClass>]
    type Static<'T> =
        static member Overloads : obj = failwith "marker"
module DlrCache =
    let count () = 0
    let clear () = ()

namespace Demo
open FSharp.Interop.Dlr
"""

let private options =
    lazy
        (// The SDK's test helper loads every *Analyzer*.dll under the current directory. Run
         // from the repo root that would include every configuration's build of the analyzer
         // (and any stale one), so use the test's own output.
         System.Environment.CurrentDirectory <- System.AppContext.BaseDirectory
         mkOptionsFromProject "net10.0" [] |> Async.AwaitTask |> Async.RunSynchronously)

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
let private sharedLine (msgs: Message list) = msgs |> List.filter (fun m -> m.Code = ReflectedDefinitionAnalyzer.SharedLineCode)

[<Fact>]
let ``two blocks on one line are reported, one per block`` () =
    let msgs =
        run """
module Impl =
    let w = box 1
    [<ReflectedDefinition>]
    let pair () : int * int = (dlr { return 1 }), (dlr { return 2 })
    [<ReflectedDefinition>]
    let fine () : int =
        let a: int = dlr { return 1 }
        let b: int = dlr { return 2 }
        a + b
    [<ReflectedDefinition>]
    let nested () : int = dlr { return (dlr { return 1 } : int) + 1 }   // one site: not reported
"""
        |> sharedLine
    msgs.Length |> should equal 2
    msgs |> List.forall (fun m -> m.Severity = Severity.Error) |> should equal true
    msgs.[0].Message |> should haveSubstring "2 dlr { } blocks start on line"
    msgs.[0].Range.StartLine |> should equal msgs.[1].Range.StartLine
    msgs.[0].Range.StartColumn |> should not' (equal msgs.[1].Range.StartColumn)


[<Fact>]
let ``a block in an inline function or member is reported`` () =
    let msgs =
        run """
module Impl =
    let w = box 1
    [<ReflectedDefinition>]
    let inline count () : int = dlr { return w?Count }
    type T() =
        [<ReflectedDefinition>]
        member inline _.Count : int = dlr { return w?Count }
        [<ReflectedDefinition>]
        member _.Fine : int = dlr { return w?Count }
"""
    let inlines = msgs |> List.filter (fun m -> m.Code = ReflectedDefinitionAnalyzer.InlineCode)
    inlines.Length |> should equal 2
    inlines |> List.forall (fun m -> m.Severity = Severity.Error) |> should equal true
    Assert.messageContains "'count'" inlines.[0] |> should equal true
    Assert.messageContains "Remove 'inline'" inlines.[0] |> should equal true
    (msgs |> List.filter (fun m -> m.Code = ReflectedDefinitionAnalyzer.Code)).Length |> should equal 0

[<Fact>]
let ``a block in a local inline function is reported; an inline function without the attribute gets DLR004 only`` () =
    let msgs =
        run """
module Impl =
    let w = box 1
    [<ReflectedDefinition>]
    let outer () : int =
        let inline local () : int = dlr { return w?Count }
        local () + local ()
    let inline unattributed () : int = dlr { return w?Count }
"""
    let codes = msgs |> List.map (fun m -> m.Code) |> List.sort
    codes |> should equal [ ReflectedDefinitionAnalyzer.InlineCode; ReflectedDefinitionAnalyzer.InlineCode ]
    Assert.messageContains "'local'" (msgs |> List.find (fun m -> m.Message.Contains "'local'")) |> should equal true

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
    let e : int = w |> Dlr.item 0
    let f : int = Dlr.Static<int>.Overloads?Parse("1")
"""
        |> outside
    msgs.Length |> should equal 6
    msgs |> List.forall (fun m -> m.Severity = Severity.Error) |> should equal true
    msgs.[0].Message |> should haveSubstring "only meaningful inside dlr { }"

[<Fact>]
let ``other Dlr-prefixed modules are not markers`` () =
    run """
module Impl =
    let n = DlrCache.count ()
    do DlrCache.clear ()
"""
    |> outside
    |> should be Empty

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

[<Fact>]
let ``argument markers are fine as call arguments and reported anywhere else`` () =
    let msgs =
        run """
module Impl =
    let w = box 1
    let kw: (string * obj) list = []
    let xs: obj list = []
    let ts: System.Type list = []
    [<ReflectedDefinition>]
    let fine () : int =
        let a: int = dlr { return w?M(1, Dlr.named {| p = 2 |}, Dlr.namedOf kw) }
        let b: int = dlr { return w?M(Dlr.typeArgs<int>(), Dlr.namedOf kw) }
        let c: int = dlr { return w |> Dlr.invoke "M" (1, Dlr.namedOf kw) }
        let d: int = dlr { return w |> Dlr.apply (Dlr.namedOf kw) }
        let e: int = dlr { return Dlr.call w (1, Dlr.namedOf kw) }
        let f: int = dlr { return Dlr.new'<int>(Dlr.namedOf kw) }
        let g: int = dlr { return (w |> Dlr.get "M") (Dlr.named {| p = 2 |}) }
        let h: int = dlr { return Dlr.get "M" w (Dlr.typeArgsOf ts, Dlr.named {| p = 2 |}) }
        let i: int = dlr { return w?M(1, Dlr.argsOf xs, Dlr.named {| p = 2 |}, Dlr.namedOf kw) }
        let j: int = dlr { return (System.DateTime.Now)?AddDays(1.0, Dlr.argsOf xs) }   // a struct-typed target expression: the tupled eta-expansion
        a + b + c + d + e + f + g + h + i + j
    [<ReflectedDefinition>]
    let wrong () : int =
        let a: int = dlr { return w?M(Dlr.namedOf kw, Dlr.namedOf kw) }          // twice
        let b: int = dlr { return w?M(1, Dlr.typeArgs<int>()) }                 // not first
        let c: obj = dlr { return box (Dlr.namedOf kw) }                        // not an argument
        let d: int = dlr { return w |> Dlr.item (Dlr.named {| p = 2 |}) }       // an indexer is not a call
        let e: int = dlr { return Dlr.call w (Dlr.typeArgsOf ts, 1) }           // type arguments on a value call
        let f: int = dlr { return Dlr.new'<int>(Dlr.namedOf kw, Dlr.namedOf kw) } // twice, through new''s obj coercions
        let g: int = dlr { let args = (1, Dlr.named {| p = 2 |}) in return w?M args }   // a let the user wrote: not followed
        let h: int = dlr { return w?M(Dlr.argsOf xs, Dlr.argsOf xs) }                 // argsOf twice
        let i: int = dlr { return w?M(Dlr.namedOf kw, Dlr.argsOf xs) }                 // positional after named
        a + b + c.GetHashCode() + d + e + f + g + h + i
"""
    let out = msgs |> List.filter (fun m -> m.Code = ReflectedDefinitionAnalyzer.ArgumentMarkerCode)
    let lines = out |> List.map (fun m -> m.Range.StartLine) |> List.sort
    let first = List.head lines
    lines |> should equal [ for i in 0 .. 8 -> first + i ]
    let messages = out |> List.map (fun m -> m.Message)
    messages |> List.filter (fun m -> m.Contains "twice") |> List.length |> should equal 3
    messages |> List.exists (fun m -> m.Contains "named arguments come last") |> should equal true
    messages |> List.exists (fun m -> m.Contains "first argument") |> should equal true
    messages |> List.exists (fun m -> m.Contains "only applies to a member call") |> should equal true
    messages |> List.filter (fun m -> m.Contains "only meaningful as an argument") |> List.length |> should equal 3
    (msgs |> List.filter (fun m -> m.Code <> ReflectedDefinitionAnalyzer.ArgumentMarkerCode)) |> should equal []

[<Fact>]
let ``a named record variable, Overloads off the target, and Dlr.call at a non-function type are reported`` () =
    let msgs =
        run """
module Impl =
    let w = box 1
    let opts = {| p = 2 |}
    [<ReflectedDefinition>]
    let fine () : int =
        let a: int = dlr { return Dlr.Static<int>.Overloads?Parse("1") }
        let b: int = dlr { return Dlr.Static<int>.Overloads |> Dlr.invoke "Parse" "1" }
        let c: int = dlr { return Dlr.invoke "Parse" "1" Dlr.Static<int>.Overloads }
        let d: int = dlr { return (Dlr.Static<int>.Overloads |> Dlr.get "Parse") "1" }
        let e: int = dlr { return Dlr.get "Parse" Dlr.Static<int>.Overloads "1" }
        let f: int = dlr { return Dlr.call w (1, 2) }
        let g: int -> int = dlr { return Dlr.call w }
        let h: int = dlr { return (w |> Dlr.call) 1 }
        let i: int = dlr { return w?M(Dlr.named {| p = 2 |}) }
        a + b + c + d + e + f + g 1 + h + i
    [<ReflectedDefinition>]
    let wrong () : int =
        let a: int = dlr { return w?M(Dlr.named opts) }                          // a record in a variable
        let b: int = dlr { return Dlr.Static<int>.Overloads?MaxValue }          // a static get
        let c: int = dlr { return Dlr.Static<int>.Overloads |> Dlr.item 0 }     // a static index
        let d: int = dlr { return Dlr.call w }                                  // read at int
        let e: int = dlr { return w |> Dlr.call }                               // piped, read at int
        let f: obj = dlr { return box Dlr.Static<int>.Overloads }              // not a target at all
        let g: bool = dlr { return w?Equals(Dlr.Static<int>.Overloads) }        // an argument, not the target
        let h: bool = dlr { return w |> Dlr.invoke "Equals" Dlr.Static<int>.Overloads }
        let i: bool = dlr { return (w |> Dlr.get "Equals") Dlr.Static<int>.Overloads }
        let j: bool = dlr { return Dlr.Static<int>.Overloads?Equals(Dlr.Static<int>.Overloads) }   // the target is fine, the argument is not
        a + b + c + d + e + f.GetHashCode() + (if g && h && i && j then 1 else 0)
"""
    let out = msgs |> List.filter (fun m -> m.Code = ReflectedDefinitionAnalyzer.ArgumentMarkerCode)
    let lines = out |> List.map (fun m -> m.Range.StartLine) |> List.sort
    let first = List.head lines
    lines |> should equal [ for i in 0 .. 9 -> first + i ]
    let messages = out |> List.map (fun m -> m.Message)
    messages |> List.filter (fun m -> m.Contains "record literal") |> List.length |> should equal 1
    messages |> List.filter (fun m -> m.Contains "target of a call") |> List.length |> should equal 7
    messages |> List.filter (fun m -> m.Contains "non-function type") |> List.length |> should equal 2
    (msgs |> List.filter (fun m -> m.Code <> ReflectedDefinitionAnalyzer.ArgumentMarkerCode)) |> should equal []

[<Fact>]
let ``outside-a-block messages name markers as the run time does`` () =
    let msgs =
        run """
module Impl =
    let w = box 1
    let a : int = w?Count
    let b : int = w |> Dlr.get "Count"
    let c = Dlr.Static<int>.Overloads
"""
        |> outside
    msgs |> List.map (fun m -> m.Message.Substring(0, m.Message.IndexOf " is only")) |> should equal [ "'?'"; "'Dlr.get'"; "'Dlr.Static<T>.Overloads'" ]

[<Fact>]
let ``a member holding a block that uses System.Void as a type argument is reported`` () =
    let msgs =
        run """
module Impl =
    let w = box 1
    [<ReflectedDefinition>]
    let withVoid () : int =
        let t = typeof<System.Void>
        ignore t
        dlr { return w?Count }
    [<ReflectedDefinition>]
    let twoBlocks () : int =
        let a: int = dlr { return w?Count }
        ignore typeof<System.Void>
        let b: int = dlr { return w?Count }
        a + b
    [<ReflectedDefinition>]
    let fine () : int =
        let t = typeof<unit>
        ignore t
        dlr { return w?Count }
    let noBlock () = typeof<System.Void>
"""
    let out = msgs |> List.filter (fun m -> m.Code = ReflectedDefinitionAnalyzer.UndecodableCode)
    out.Length |> should equal 3                                                  // one per block
    out |> List.forall (fun m -> m.Severity = Severity.Error) |> should equal true
    Assert.messageContains "'withVoid'" out.[0] |> should equal true
    Assert.messageContains "'twoBlocks'" out.[1] |> should equal true
    Assert.messageContains "'twoBlocks'" out.[2] |> should equal true
    (msgs |> List.filter (fun m -> m.Code <> ReflectedDefinitionAnalyzer.UndecodableCode)) |> should equal []

