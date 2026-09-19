/// What only `dlrq { }` needs: no [<ReflectedDefinition>] on this module or anything in it. The
/// shared semantics are covered by Tests.Quoted, which compiles the other test files with `dlr`
/// shadowed by `dlrq`.
module Tests.Quoted

open System
open System.Threading.Tasks
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

#if DLRQ
[<Fact>]
let ``in Tests.Quoted every dlr is a dlrq`` () =
    dlr.GetType() |> should equal typeof<DlrQuotedBuilder>
#endif

/// Module-level code has no reflected definition, whatever attributes are around it.
let private atModuleLevel: int = dlrq { return (box (Widget()))?Count }

[<Fact>]
let ``needs no attribute, including in module-level code`` () =
    let w = box (Widget())
    (dlrq { return w?Count } : int) |> should equal 3
    atModuleLevel |> should equal 3

/// Nothing here could be quoted: a byref parameter and an inner generic function are both
/// rejected under [<ReflectedDefinition>] (the reason a dlr { } would have had to move out).
let private unquotable (w: obj) (n: byref<int>) : int =
    let id (x: 'a) = x
    n <- n + 1
    let local = id n
    let label = id "x"   // the inner function is generic: used at two types
    dlrq { return w?Add(local, label.Length) }

[<Fact>]
let ``works inside a function that cannot be quoted`` () =
    let mutable n = 1
    unquotable (box (Widget())) &n |> should equal 3

[<Fact>]
let ``captured values: several, the same one twice, inside loop bodies and lambdas, and this`` () =
    let w = box (Widget())
    let a, b = 1, 2
    (dlrq { return w?Add(a, b) + w?Add(b, a) } : int) |> should equal 6
    let hits = ResizeArray<int>()
    dlrq {
        for i in [ a; b ] do
            hits.Add(w?Add(i, a))
    }
    List.ofSeq hits |> should equal [ 2; 3 ]
    let f: int -> int = dlrq { return (fun x -> w?Add(x, b)) }
    f 40 |> should equal 42

type private Probe(w: obj) =
    member _.Extra = 10
    member this.Total: int = dlrq { return w?Count + this.Extra }

[<Fact>]
let ``this in a member is a capture`` () =
    Probe(box (Widget())).Total |> should equal 13

[<Fact>]
let ``a captured mutable is read as the value at the call`` () =
    // Assigning it in the block is a compile error (FS3155); reading is a snapshot per call.
    let w = box (Widget())
    let mutable m = 5
    let read () : int = dlrq { return w?Add(m, 1) }
    read () |> should equal 6
    m <- 7
    read () |> should equal 8

let private echo<'T> (w: obj) (v: 'T) : 'T = dlrq { return w?Echo(v) }

[<Fact>]
let ``a generic enclosing function gets one entry per instantiation at the site`` () =
    let w = box (Widget())
    DlrCache.clear ()
    echo w "s" |> should equal "s"
    echo w 7 |> should equal 7
    echo w "t" |> should equal "t"
    DlrCache.count () |> should equal 2

[<Fact>]
let ``a site compiles once, and again after clear`` () =
    let w = box (Widget())
    let read (n: int) : int = dlrq { return w?Add(n, 1) }
    DlrCache.clear ()
    for i in 1 .. 100 do read i |> should equal (i + 1)
    DlrCache.count () |> should equal 1
    DlrCache.clear ()
    DlrCache.count () |> should equal 0
    read 1 |> should equal 2
    DlrCache.count () |> should equal 1

[<Fact>]
let ``nested blocks compile into the outer one, whichever kind each is`` () =
    let w = box (Widget())
    (dlrq { return (dlrq { return w?Count } : int) + 1 } : int) |> should equal 4
    // A dlr { } inside needs no reflected definition either: it is part of the dlrq's quotation.
    (dlrq { return (dlr { return w?Count } : int) + 2 } : int) |> should equal 5
    DlrCache.clear ()
    (dlrq { return (dlrq { return w?Count } : int) } : int) |> should equal 3
    DlrCache.count () |> should equal 1

[<ReflectedDefinition>]
let private quotedInsideReflected (w: obj) : int = dlr { return (dlrq { return w?Count } : int) * 2 }

[<Fact>]
let ``a dlrq inside a dlr compiles with it`` () =
    quotedInsideReflected (box (Widget())) |> should equal 6

[<Fact>]
let ``inside task and async`` () =
    let w = box (Widget())
    let t = task { return (dlrq { return w?Count } : int) }
    t.Result |> should equal 3
    let a = async { return (dlrq { return w?Name } : string) }
    // StartImmediateAsTask rather than RunSynchronously, which the single-threaded wasm runtime cannot do.
    (Async.StartImmediateAsTask a).Result |> should equal "widget"

[<Fact>]
let ``two blocks on one line are told apart by shape, including which variable they use`` () =
    // The one-per-line rule (DLR003) still applies as a lint; what saves these is that their
    // quotations differ — in a literal, or only in which bound variable is returned — so each
    // gets its own entry.
    let w = box (Widget())
    // fsharpanalyzer: ignore-line-next DLR003
    let pair: int * string = (dlrq { return w?Count }), (dlrq { return w?Name })
    pair |> should equal (3, "widget")
    // fsharpanalyzer: ignore-line-next DLR003
    let vars: int * int = (dlrq { let x: int = w?Count in let y: int = w?Add(1, 1) in return x + y * 0 }), (dlrq { let x: int = w?Count in let y: int = w?Add(1, 1) in return y + x * 0 })
    vars |> should equal (3, 2)

[<Fact>]
let ``let rec with captures in the definition and the body, of different types`` () =
    // The slots are numbered at compile time and filled per call by two traversals; a let rec
    // is the one node where FSharp.Core's shape puts the body before the definitions.
    let w = box (Widget())
    let n, s = 1, "abc"
    let r: int =
        dlrq {
            let rec f (k: int) : int = if k = 0 then w?Add(n, 1) else f (k - 1)
            return f 2 + s.Length
        }
    r |> should equal 5
    let t: string =
        dlrq {
            let rec g (k: int) : string = if k = 0 then s else g (k - 1)
            return g 1 + string (w?Add(n, n) : int)
        }
    t |> should equal "abc2"

type private Holder<'T>() =
    static member val P = typeof<'T>.Name with get, set
    static member val F = typeof<'T>.Name

let private setHolder<'T> (w: obj) : unit = dlrq { ignore (w?Count : int); Holder<'T>.P <- "set" }
let private readHolder<'T> (w: obj) : string = dlrq { ignore (w?Count : int); return Holder<'T>.F }

[<Fact>]
let ``a type mentioned only by a static property set or get is part of the shape`` () =
    let w = box (Widget())
    setHolder<int> w
    setHolder<string> w
    Holder<int>.P |> should equal "set"
    Holder<string>.P |> should equal "set"
    readHolder<int> w |> should equal "Int32"
    readHolder<string> w |> should equal "String"

[<Fact>]
let ``a fresh builder instance is still one site`` () =
    let w = box (Widget())
    DlrCache.clear ()
    for _ in 1 .. 20 do
        let b = DlrQuotedBuilder()
        (b { return w?Count } : int) |> should equal 3
    DlrCache.count () |> should equal 1

// Needs real threads: on single-threaded wasm Parallel.For runs sequentially and would prove
// nothing (AnyUnit has no threading capability yet).
[<Fact>]
let ``concurrent first calls compile once`` () =
    if Environment.ProcessorCount < 2 then raise (AnyUnit.IgnoreException "needs more than one thread")
    let w = box (Widget())
    DlrCache.clear ()
    let read (n: int) : int = dlrq { return w?Add(n, 1) }
    let results = Array.zeroCreate 64
    Parallel.For(0, 64, fun i -> results.[i] <- read i) |> ignore
    results |> should equal [| for i in 0 .. 63 -> i + 1 |]
    DlrCache.count () |> should equal 1

[<Fact>]
let ``errors are the same kinds`` () =
    let w = box (Widget())
    (fun () -> (dlrq { return w?Nope } : int) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> (dlrq { return Dlr.Static<Widget>.Overloads } : obj) |> ignore) |> should throw typeof<DlrTranslationException>
