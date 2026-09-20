/// Explicit generic type arguments: `Dlr.typeArgs<A, B>()` and `Dlr.typeArgsOf [ … ]`, whose list may be a run-time value.
[<ReflectedDefinition>]
module Tests.TypeArgs

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``explicit type argument when it cannot be inferred`` () =
    let w: obj = Widget()
    let name: string = dlr { return w?TypeName(Dlr.typeArgs<int>()) }
    let zero: int = dlr { return w?Default(Dlr.typeArgs<int>()) }
    name |> should equal "Int32"
    zero |> should equal 0

[<Fact>]
let ``two explicit type arguments with positional args`` () =
    let w: obj = Widget()
    let pair: string = dlr { return w?Pair(Dlr.typeArgs<obj, string>(), 1, "x") }
    pair |> should equal "Object/String"

[<Fact>]
let ``type argument inference still works without the marker`` () =
    let w: obj = Widget()
    let echoed: int = dlr { return w?Echo(41) }
    echoed |> should equal 41

[<Fact>]
let ``wrong type argument arity raises RuntimeBinderException`` () =
    let w: obj = Widget()
    (fun () -> (dlr { return w?TypeName(Dlr.typeArgs<int, int>()) } : string) |> ignore)
    |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``typeArgsOf takes a literal list of any length`` () =
    let w: obj = Widget()
    let one: string = dlr { return w?TypeName(Dlr.typeArgsOf [ typeof<int> ]) }
    let two: string = dlr { return w?Pair(Dlr.typeArgsOf [ typeof<obj>; typeof<string> ], 1, "x") }
    let five: string = dlr { return w?FiveNames(Dlr.typeArgsOf [ typeof<int>; typeof<string>; typeof<float>; typeof<bool>; typeof<char> ]) }
    one |> should equal "Int32"
    two |> should equal "Object/String"
    five |> should equal "Int32/String/Double/Boolean/Char"
    // An empty list is no type arguments: inference as without the marker.
    let inferred: int = dlr { return w?Echo(Dlr.typeArgsOf [], 41) }
    inferred |> should equal 41

[<Fact>]
let ``typeArgsOf with a list only known at run time`` () =
    let w: obj = Widget()
    let name (t: Type) : string = dlr { return w?TypeName(Dlr.typeArgsOf [ t ]) }
    name typeof<int> |> should equal "Int32"
    name typeof<string> |> should equal "String"
    name typeof<int> |> should equal "Int32"                        // one site, alternating lists
    let pair (a: Type) (b: Type) : string = dlr { return w?Pair(Dlr.typeArgsOf [ a; b ], 1, "x") }
    pair typeof<obj> typeof<string> |> should equal "Object/String"
    pair typeof<int> typeof<obj> |> should equal "Int32/Object"
    // Combined with a computed name: the key is both.
    let call (m: string) (t: Type) : string = dlr { return (?) w m (Dlr.typeArgsOf [ t ]) }
    call "TypeName" typeof<float> |> should equal "Double"
    call "TypeName" typeof<int> |> should equal "Int32"
    // An empty runtime list is inference; the wrong count is the binder's error.
    let echo (ts: Type list) : int = dlr { return w?Echo(Dlr.typeArgsOf ts, 41) }
    echo [] |> should equal 41
    let names (ts: Type list) : string = dlr { return w?TypeName(Dlr.typeArgsOf ts) }
    (fun () -> names [ typeof<int>; typeof<int> ] |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``typeArgs must come first`` () =
    let w: obj = Widget()
    // The analyzer reports this at build time (DLR005); this pins the run-time error behind it.
    // fsharpanalyzer: ignore-line-next DLR005
    (fun () -> (dlr { return w?Pair(1, Dlr.typeArgs<int, int>()) } : string) |> ignore)
    |> should throw typeof<DlrTranslationException>
