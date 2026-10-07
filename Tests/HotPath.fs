/// What the README's numbers imply and nothing else pinned: a bound call allocates nothing per
/// call on the hot path, and first use of a site is safe under concurrency.
[<ReflectedDefinition>]
module Tests.HotPath

open System
open System.Threading
open System.Threading.Tasks
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

let private allocatedBy (n: int) (run: unit -> unit) =
    run ()                                               // bind, compile, convert: not counted
    GC.Collect()
    let before = GC.GetAllocatedBytesForCurrentThread()
    for _ in 1 .. n do run ()
    (GC.GetAllocatedBytesForCurrentThread() - before) / int64 n

[<Fact>]
let ``a bound call allocates nothing but, for a value result, its box`` () : unit =
#if DEBUG
    raise (AnyUnit.IgnoreException "allocation counts are for the Release build (Debug runs the closure fallback, which allocates the Delay closure per call)")
#else
    if string System.Runtime.InteropServices.RuntimeInformation.OSArchitecture = "Wasm" then
        raise (AnyUnit.IgnoreException "allocation counts are for the JIT runtimes")
    let w = box (Widget())
    // Per call: nothing for the block itself — it is a struct state machine on the stack, not a
    // closure — plus one box when the operation's result is a value type, because a DLR site
    // returns `obj` (C# `dynamic` boxes the same way). The compiled delegate, the sites and
    // their rules allocate nothing once bound; anything past these numbers is a regression (a
    // boxing conversion, a per-call closure in a rule, a block falling back to its closure).
    let box' = 24L
    let get () = (dlr { return w?Count } : int) |> ignore
    let call () = (dlr { return w?Add(1, 2) } : int) |> ignore
    let unitCall () = dlr { w?Touch() }                             // void site: nothing to box
    let set () = dlr { w?Count <- 3 }                               // SetMember returns the value: boxed
    let stringGet () = (dlr { return w?Name } : string) |> ignore   // a reference result: no box
    let bytes = allocatedBy 1000
    bytes unitCall |> should equal 0L
    bytes stringGet |> should equal 0L
    bytes get |> should lessThanOrEqualTo box'
    bytes call |> should lessThanOrEqualTo box'
    bytes set |> should lessThanOrEqualTo box'
    // Dlr.out: the outs come back in a struct holder, unboxed; what is left is the result's box,
    // and the F# tuple itself unless the result is a struct tuple.
    let d = box (Collections.Generic.Dictionary<string, int>(dict [ "a", 1 ]))
    let outStruct () = (dlr { return d?TryGetValue("a", Dlr.out) } : struct (bool * int)) |> ignore
    let outTuple () = (dlr { return d?TryGetValue("a", Dlr.out) } : bool * int) |> ignore
    bytes outStruct |> should lessThanOrEqualTo box'
    bytes outTuple |> should lessThanOrEqualTo (box' + 24L)
    // A member read as a function past five, applied (read once, outside): the result's box plus
    // what F# itself allocates — the tuple (40 B), or a step per argument after the first (no
    // InvokeFast past OptimizedClosures), each one object of 40 B: the step before, its argument
    // and `next`. No closure, delegate or array per step (once ~1 KB).
    let tupled6: int * int * int * int * int * int -> int = dlr { return w?Sum6 }
    let curried6: int -> int -> int -> int -> int -> int -> int = dlr { return w?Sum6 }
    let applyTupled () = tupled6 (1, 2, 3, 4, 5, 6) |> ignore
    let applyCurried () = curried6 1 2 3 4 5 6 |> ignore
    bytes applyTupled |> should lessThanOrEqualTo (box' + 40L)
    bytes applyCurried |> should lessThanOrEqualTo (box' + 5L * 40L)
    // A member read as a delegate type (#201): a delegate-typed property is C#'s read, and stays
    // free; a method is the delegate over an invoker, made per read: the invoker (40 B: the two
    // sites and the target) and the delegate (64 B on .NET 10; .NET 11's is 8 B smaller, hence a
    // bound). A call through it, read once, costs the result's box, as a call does.
    let h = box (Holders())
    let delegateProperty () = (dlr { return h?AsDelegate } : Func<int>) |> ignore
    let methodAsDelegate () = (dlr { return w?Add } : Func<int, int, int>) |> ignore
    let add: Func<int, int, int> = dlr { return w?Add }
    let invokeDelegate () = add.Invoke(1, 2) |> ignore
    bytes delegateProperty |> should equal 0L
    bytes methodAsDelegate |> should lessThanOrEqualTo 104L
    bytes invokeDelegate |> should lessThanOrEqualTo box'
#endif

[<Fact>]
let ``first use of one site under concurrency compiles once and binds correctly`` () =
    if Environment.ProcessorCount < 2 then raise (AnyUnit.IgnoreException "needs more than one thread")
    let w = box (Widget())
    let before = DlrCache.count ()
    let results = Array.zeroCreate<int> 64
    let gate = new ManualResetEventSlim(false)
    let workers =
        [| for i in 0 .. 63 ->
            Task.Run(fun () ->
                gate.Wait()
                results.[i] <- (dlr { return w?Add(i, 1) } : int)) |]
    gate.Set()
    Task.WaitAll workers
    results |> should equal [| for i in 0 .. 63 -> i + 1 |]
    DlrCache.count () - before |> should equal 1              // one block, one entry (a racing duplicate compile is dropped)

[<Fact>]
let ``computed names under concurrency: distinct names, one site, right answers`` () =
    if Environment.ProcessorCount < 2 then raise (AnyUnit.IgnoreException "needs more than one thread")
    let w = box (Widget())
    let names = [| "Count"; "Name"; "Touched" |]
    let read (name: string) : obj = dlr { return (?) w name }
    let results = Array.zeroCreate<string> 300
    Parallel.For(0, 300, fun i -> results.[i] <- string (read names.[i % 3])) |> ignore
    results |> Array.forall (fun r -> r = "3" || r = "widget" || r = "0") |> should equal true
    results |> Array.distinct |> Array.length |> should equal 3
