/// The blocks the docs show compiled. Each sits between `// example: <name>` and `// end`; the
/// generator copies that source into the docs beside its tree.
module Trees.Examples

open System.Collections.Generic
open FSharp.Interop.Dlr

type Widget() =
    member val Count = 3 with get, set
    member val Name = "widget" with get, set
    member _.Add(a: int, b: int) = a + b
    member _.Touch() = ()
    member _.Run(f: System.Func<int, int>) = f.Invoke 20
    member _.Bump(count: int, ?step: int) = count + defaultArg step 1
    member _.Double(n: byref<int>) = n <- n * 2
    static member Twice(n: int) = n * 2

// example: read
[<ReflectedDefinition>]
let read (o: obj) : int = dlr { return o?Count }
// end

// example: call
[<ReflectedDefinition>]
let call (o: obj) (x: int) : int = dlr { return o?Add(x, 1) }
// end

// example: loop
[<ReflectedDefinition>]
let loop (o: obj) (items: int list) : int =
    dlr {
        let mutable total = 0
        for i in items do
            try total <- total + o?Add(i, 1)
            with _ -> ()
        return total
    }
// end

// example: computed
[<ReflectedDefinition>]
let computed (o: obj) (name: string) : obj = dlr { return (?) o name }
// end

// example: asFunction
[<ReflectedDefinition>]
let asFunction (o: obj) : int -> int -> int = dlr { return o?Add }
// end

// example: outArg
[<ReflectedDefinition>]
let outArg (d: obj) : bool * int = dlr { return d?TryGetValue("k", Dlr.out) }
// end

// example: set
[<ReflectedDefinition>]
let set (o: obj) (name: string) = dlr { o?Name <- name }
// end

// example: discarded
[<ReflectedDefinition>]
let touch (o: obj) = dlr { o?Touch() }
// end

// example: operator
[<ReflectedDefinition>]
let plus (a: obj) (b: obj) : int = dlr { return a ?+? b }
// end

// example: named
[<ReflectedDefinition>]
let named (o: obj) (x: int) : int = dlr { return o?Add(Dlr.named {| b = 1; a = x |}) }
// end

// example: namedOf
[<ReflectedDefinition>]
let namedOf (o: obj) (kwargs: (string * obj) list) : int = dlr { return o?Add(Dlr.namedOf kwargs) }
// end

// example: argsOf
[<ReflectedDefinition>]
let argsOf (o: obj) (args: obj list) : int = dlr { return o?Add(Dlr.argsOf args) }
// end

// example: constructor
[<ReflectedDefinition>]
let make () : obj = dlr { return Dlr.new'<Widget>() }
// end

// example: static
[<ReflectedDefinition>]
let twice (x: obj) : int = dlr { return Dlr.Static<Widget>.Overloads?Twice(x) }
// end

// example: indexer
[<ReflectedDefinition>]
let item (d: obj) : int = dlr { return d |> Dlr.item "k" }
// end

// example: lambda
[<ReflectedDefinition>]
let lambda (o: obj) : int = dlr { return o?Run(fun x -> x + 1) }
// end

// example: optional
[<ReflectedDefinition>]
let optional (o: obj) : int = dlr { return o?Bump(1) }
// end

// example: ref
[<ReflectedDefinition>]
let byRef (o: obj) : int =
    let mutable n = 21
    dlr { o?Double(Dlr.ref n) }
    n
// end

/// Each example by name, run once so the library compiles it.
let all : (string * (unit -> unit)) list =
    let w = box (Widget())
    [ "read", (fun () -> read w |> ignore)
      "call", (fun () -> call w 41 |> ignore)
      "loop", (fun () -> loop w [ 1; 2; 3 ] |> ignore)
      "computed", (fun () -> computed w "Count" |> ignore)
      "asFunction", (fun () -> asFunction w 40 2 |> ignore)
      "outArg", (fun () -> outArg (box (Dictionary<string, int>(dict [ "k", 7 ]))) |> ignore)
      "set", (fun () -> set w "renamed")
      "discarded", (fun () -> touch w)
      "operator", (fun () -> plus (box 1) (box 2) |> ignore)
      "named", (fun () -> named w 41 |> ignore)
      "namedOf", (fun () -> namedOf w [ "a", box 40; "b", box 2 ] |> ignore)
      "argsOf", (fun () -> argsOf w [ box 40; box 2 ] |> ignore)
      "constructor", (fun () -> make () |> ignore)
      "static", (fun () -> twice (box 21) |> ignore)
      "indexer", (fun () -> item (box (Dictionary<string, int>(dict [ "k", 7 ]))) |> ignore)
      "lambda", (fun () -> lambda w |> ignore)
      "optional", (fun () -> optional w |> ignore)
      "ref", (fun () -> byRef w |> ignore) ]
