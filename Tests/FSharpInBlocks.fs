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
let ``ranges on every primitive, in the builder's for and as values, are F#'s`` () =
    let o = box (Widget())   // Count = 3
    // The builder's `for` over an int range is a counted loop (#240); every other range is F#'s typed one.
    let up: int =
        dlr {
            let mutable s = 0
            for i in 1 .. o?Count do s <- s + i
            return s
        }
    up |> should equal 6
    let empty: int =
        dlr {
            let mutable s = 0
            for i in o?Count .. 1 do s <- s + i
            return s
        }
    empty |> should equal 0
    let stepped: int list =
        dlr {
            let mutable s = []
            for i in 1 .. 2 .. o?Count + 4 do s <- i :: s
            return s
        }
    stepped |> should equal [ 7; 5; 3; 1 ]
    let down: int list =
        dlr {
            let mutable s = []
            for i in o?Count + 4 .. -3 .. 0 do s <- i :: s
            return s
        }
    down |> should equal [ 1; 4; 7 ]
    let longs: int64 list =
        dlr {
            let mutable s = []
            for i in 1L .. (o?Count : int64) do s <- i :: s
            return s
        }
    longs |> should equal [ 3L; 2L; 1L ]
    let chars: string =
        dlr {
            let mutable s = ""
            for c in 'a' .. 'c' do s <- s + string c
            return s + string (o?Count : int)
        }
    chars |> should equal "abc3"
    (dlr { return [ 1 .. (o?Count : int) ] } : int list) |> should equal [ 1; 2; 3 ]
    (dlr { return [ 0uy .. 100uy .. 255uy ] @ [ 250uy .. byte (o?Count : int) ] } : byte list) |> should equal [ 0uy; 100uy; 200uy ]
    (dlr { return Seq.toList (seq { 1s .. int16 (o?Count : int) }) } : int16 list) |> should equal [ 1s; 2s; 3s ]
    (dlr { return ([ 1u .. uint32 (o?Count : int) ] |> List.map int) @ ([ 1UL .. 1UL ] |> List.map int) } : int list) |> should equal [ 1; 2; 3; 1 ]
    (dlr { return [ -1y .. 1y ] |> List.map int } : int list) |> should equal [ -1; 0; 1 ]
    (dlr { return [ 1us .. 2us ] |> List.map int } : int list) |> should equal [ 1; 2 ]
    (dlr { return [ nativeint 1 .. nativeint (o?Count : int) ] @ [ nativeint 3 .. nativeint 2 .. nativeint 5 ] |> List.map int } : int list) |> should equal [ 1; 2; 3; 3; 5 ]
    (dlr { return [ unativeint 1 .. unativeint 2 ] |> List.map int } : int list) |> should equal [ 1; 2 ]
    (dlr { return [ 0.5 .. (o?Count : float) ] } : float list) |> should equal [ 0.5; 1.5; 2.5 ]
    (dlr { return [ 1.0f .. 0.5f .. 2.0f ] } : float32 list) |> should equal [ 1.0f; 1.5f; 2.0f ]
    (dlr { return [ 1m .. (o?Count : decimal) ] } : decimal list) |> should equal [ 1m; 2m; 3m ]   // no typed range: the generic one

[<Fact>]
let ``a range's bounds are evaluated once, low first`` () =
    let o = box (Widget())
    let log = Collections.Generic.List<string>()
    let bound (name: string) (v: int) = log.Add name; v
    let n: int =
        dlr {
            let mutable n = 0
            for _ in bound "low" 1 .. bound "high" (o?Count : int) do n <- n + 1
            return n
        }
    n |> should equal 3
    (dlr { return [ bound "low" 1 .. bound "step" 1 .. bound "high" 2 ] } : int list) |> should equal [ 1; 2 ]
    List.ofSeq log |> should equal [ "low"; "high"; "low"; "step"; "high" ]

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
let ``a try returning a function, and use under a lambda`` () =
    // A function-typed try body: a parameterless delegate whose body is a lambda (builder and raw).
    let viaBuilder: int -> int = dlr { try return (fun x -> x + 1) with _ -> return (fun x -> x - 1) }
    viaBuilder 41 |> should equal 42
    let asValue: int -> int = dlr { return (try (fun x -> x + 2) with _ -> (fun x -> x - 2)) }
    asValue 40 |> should equal 42
    // `use` under a lambda: its try … finally disposes per call.
    let log = Collections.Generic.List<string>()
    let total: int = dlr { return [ 1; 2 ] |> List.sumBy (fun x -> use d = new Disposal(log, string x) in x * 10) }
    total |> should equal 30
    List.ofSeq log |> should equal [ "disposed 1"; "disposed 2" ]

[<Fact>]
let ``async and task inside a block`` () =
    let o = box (Widget())
    // StartImmediateAsTask, not RunSynchronously: on single-threaded browser-wasm the latter would
    // block the only thread (see Nesting.fs).
    (dlr { return (Async.StartImmediateAsTask (async { return (o?Name : string) })).Result } : string) |> should equal "widget"
    (dlr { return (task { return (o?Name : string) }).Result } : string) |> should equal "widget"
