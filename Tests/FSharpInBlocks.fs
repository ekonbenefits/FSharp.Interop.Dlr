/// Ordinary F# written inside a block: the translator hands it to LeafExpressionConverter, and
/// this pins what that accepts. Each case is something a user would reach for, either working
/// or (none so far) a DlrTranslationException naming the construct. `sprintf`, interpolation,
/// `match` with type tests, `seq { }`, `async`/`task` inside a block — all fine.
[<ReflectedDefinition>]
module Tests.FSharpInBlocks

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

type R = { X: int; Y: string }
type U =
    | A of int
    | B of string

[<Fact>]
let ``strings: sprintf, interpolation, concatenation, String.Format`` () =
    let o = box (Widget())
    (dlr { return sprintf "%d/%s" (o?Count : int) (o?Name : string) } : string) |> should equal "3/widget"
    (dlr { return $"{(o?Count : int)}-{(o?Name : string)}" } : string) |> should equal "3-widget"
    (dlr { return (o?Name : string) + "!" + string (o?Count : int) } : string) |> should equal "widget!3"
    (dlr { return String.Format("{0}:{1}", (o?Count : int), (o?Name : string)) } : string) |> should equal "3:widget"

[<Fact>]
let ``match: literals, type tests, and if as an expression`` () =
    let o = box (Widget())
    let kind: string = dlr { return (match (o?Name : string) with "widget" -> "w" | "gadget" -> "g" | _ -> "?") }
    kind |> should equal "w"
    let boxed = box (Holders())
    let described: string =
        dlr {
            return
                match (boxed?AsObj : obj) with
                | :? (int -> int) as f -> "function " + string (f 1)
                | :? string as s -> s
                | _ -> "other"
        }
    described |> should equal "function 3"
    (dlr { return (if (o?Count : int) > 1 then "big" else "small") } : string) |> should equal "big"

[<Fact>]
let ``F# data built from dynamic results: option, record, union, tuple, list, array`` () =
    let o = box (Widget())
    (dlr { return Some (o?Count : int) } : int option) |> should equal (Some 3)
    (dlr { return { X = o?Count; Y = o?Name } } : R) |> should equal { X = 3; Y = "widget" }
    (dlr { return A (o?Count : int) } : U) |> should equal (A 3)
    (dlr { return ((o?Count : int), (o?Name : string)) } : int * string) |> should equal (3, "widget")
    (dlr { return [ (o?Count : int); 1 ] } : int list) |> should equal [ 3; 1 ]
    (dlr { return [| (o?Count : int); 1 |].[0] } : int) |> should equal 3
    let (a, b) : int * string = dlr { let (a, b) = ((o?Count : int), (o?Name : string)) in return (a, b) }
    (a, b) |> should equal (3, "widget")

[<Fact>]
let ``collections: comprehensions, seq expressions, List.map with a lambda, for over an array and a seq`` () =
    let o = box (Widget())
    (dlr { return [ for i in 1 .. 2 -> i + (o?Count : int) ] } : int list) |> should equal [ 4; 5 ]
    (dlr { return List.ofSeq (seq { for i in 1 .. 2 -> i + (o?Count : int) }) } : int list) |> should equal [ 4; 5 ]
    (dlr { return [ 1; 2 ] |> List.map (fun i -> i + (o?Count : int)) } : int list) |> should equal [ 4; 5 ]
    (dlr { return Some 1 |> Option.map (fun i -> i + (o?Count : int)) } : int option) |> should equal (Some 4)
    let overArray: int =
        dlr {
            let mutable s = 0
            for x in [| 1; 2 |] do s <- s + x + (o?Count : int)
            return s
        }
    overArray |> should equal 9
    let overSeq: int =
        dlr {
            let mutable s = 0
            for x in Seq.init 2 id do s <- s + x + (o?Count : int)
            return s
        }
    overSeq |> should equal 7

[<Fact>]
let ``failwith and raise propagate as themselves`` () =
    let o = box (Widget())
    (fun () -> (dlr { return (if (o?Count : int) > 2 then failwith "too many" else "ok") } : string) |> ignore)
    |> should throw typeof<Exception>
    (fun () -> (dlr { return (if (o?Count : int) > 2 then raise (ArgumentException "arg") else "ok") } : string) |> ignore)
    |> should throw typeof<ArgumentException>
    // and can be caught inside the block, with a filter that does not match falling through
    let r: string =
        dlr {
            try return (if (o?Count : int) > 2 then failwith "too many" else "ok")
            with :? ArgumentException -> return "arg"
               | e -> return "caught " + e.Message
        }
    r |> should equal "caught too many"

[<Fact>]
let ``try with and try finally anywhere: under a lambda, in a delegate literal, as a value`` () =
    // Not the builder's own `try` (#158): a raw TryWith / TryFinally node, run through the same delegates.
    let o = box (Widget())
    let safe: int list = dlr { return [ 0; 2; 5 ] |> List.map (fun x -> try 100 / x with _ -> -1) }
    safe |> should equal [ -1; 50; 20 ]
    let divide: Func<int, int> = dlr { return Func<int, int>(fun x -> try 100 / x with :? DivideByZeroException -> 0) }
    divide.Invoke 0 |> should equal 0
    divide.Invoke 4 |> should equal 25
    let value: int = dlr {
        let n = (try (o?Count : int) / 0 with _ -> -(o?Count : int))       // a value, markers on both sides
        return n }
    value |> should equal -3
    let mutable cleaned = 0
    let total: int = dlr { return [ 1; 2 ] |> List.sumBy (fun x -> try x * 10 finally cleaned <- cleaned + 1) }
    total |> should equal 30
    cleaned |> should equal 2

[<Fact>]
let ``an exception no case matches is rethrown as itself, with its stack trace`` () =
    let o = box (Widget())
    let thrown = InvalidOperationException "original"
    // Under a lambda: F# quotes the unmatched case as `reraise ()`.
    let caught =
        try
            dlr { return [ 1 ] |> List.map (fun _ -> try raise thrown with :? ArgumentException -> 0) } |> ignore
            None
        with e -> Some e
    caught |> should equal (Some (thrown :> exn))
    // The builder's own `try` too, and an explicit `reraise ()`.
    let viaBuilder =
        try
            (dlr {
                try return (if (o?Count : int) > 0 then raise thrown else 0)
                with :? ArgumentException -> return -1 } : int) |> ignore
            None
        with e -> Some e
    viaBuilder |> should equal (Some (thrown :> exn))
    let explicit' =
        try
            dlr { return [ 1 ] |> List.map (fun _ -> try raise thrown with _ -> reraise ()) } |> ignore
            None
        with e -> Some e
    explicit' |> should equal (Some (thrown :> exn))
    thrown.StackTrace |> should not' (be NullOrEmptyString)
    // A nested try in a handler keeps its own handler's rethrow.
    let nested: int = dlr { return [ 0 ] |> List.sumBy (fun x -> try 1 / x with _ -> (try failwith "inner" with _ -> 7)) }
    nested |> should equal 7

[<Fact>]
let ``async and task inside a block`` () =
    let o = box (Widget())
    // StartImmediateAsTask, not RunSynchronously: on single-threaded browser-wasm the latter would
    // block the only thread (see Nesting.fs).
    (dlr { return (Async.StartImmediateAsTask (async { return (o?Name : string) })).Result } : string) |> should equal "widget"
    (dlr { return (task { return (o?Name : string) }).Result } : string) |> should equal "widget"
