[<ReflectedDefinition>]
module Tests.Nesting

open System.Threading.Tasks
open FSharp.Interop.Dlr
open AnyUnit                        // TestCapabilities and RequiresCapability, since 1.3.0
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit

[<Fact>]
let ``nested dlr blocks`` () =
    let w: obj = Widget()
    let n: int = dlr { return (dlr { return w?Count } : int) + w?Count }
    n |> should equal 6

// These two genuinely suspend, so they hand their Task to the engine rather than blocking on it:
// on single-threaded browser-wasm a blocking wait can never be satisfied (the continuation needs
// the thread), and the engine reports the declared requirement as Ignored there instead.
[<Fact; RequiresCapability(TestCapabilities.AsyncYield)>]
let ``dlr inside task`` () : System.Threading.Tasks.Task =
    let w: obj = Widget()
    task {
        do! System.Threading.Tasks.Task.Yield()
        let n: int = dlr { return w?Count }
        n |> should equal 3
    }

[<Fact; RequiresCapability(TestCapabilities.AsyncYield)>]
let ``dlr inside async`` () : System.Threading.Tasks.Task =
    let w: obj = Widget()
    async {
        do! Async.Sleep 1
        let n: int = dlr { return w?Count }
        n |> should equal 3
    }
    |> Async.StartAsTask :> System.Threading.Tasks.Task

[<Fact>]
let ``dlr inside task and async that complete synchronously`` () =
    let w: obj = Widget()
    let t = task { return (dlr { return w?Count } : int) }
    t.Result |> should equal 3
    // StartImmediate runs on the current thread up to the first real suspension, so a
    // never-suspending workflow comes back completed even on single-threaded browser-wasm,
    // where RunSynchronously would block the only thread.
    let a = async { return (dlr { return w?Add(1, 2) } : int) }
    (Async.StartImmediateAsTask a).Result |> should equal 3

// Awaiting a dynamic call: the block is synchronous and returns what the member returns,
// converted to the awaitable type named; the builder awaits that as it would any other.
[<Fact>]
let ``a block's result is awaited in task at the type named; an unknown result type as Task then ?Result`` () =
    let svc: obj = Service()
    let t =
        task {
            let! a = (dlr { return svc?GetAsync(20) } : Task<int>)
            let pending: Task = dlr { return svc?GetAsync(21) }
            do! pending
            let b: int = dlr { return pending?Result }
            do! (dlr { return svc?RunAsync() } : Task)
            let! c = (dlr { return svc?GetValueTask(41) } : ValueTask<int>)
            return a, b, c
        }
    t.Result |> should equal (40, 42, 42)

[<Fact>]
let ``a block's result is awaited in async, an F# Async member directly`` () =
    let svc: obj = Service()
    let a =
        async {
            let! a = (dlr { return svc?GetAsync(20) } : Task<int>) |> Async.AwaitTask
            let pending: Task = dlr { return svc?GetAsync(21) }
            do! Async.AwaitTask pending
            let b: int = dlr { return pending?Result }
            let! c = (dlr { return svc?GetValueTask(41) } : ValueTask<int>).AsTask() |> Async.AwaitTask
            let! d = (dlr { return svc?FetchAsync(1) } : Async<int>)
            return a, b, c, d
        }
    (Async.StartImmediateAsTask a).Result |> should equal (40, 42, 42, 101)

[<Fact>]
let ``nested dlr blocks on separate lines share the outer site`` () =
    let w: obj = Widget()
    let before = DlrCache.count ()
    let f () : string =
        dlr {
            let n: int = dlr { return w?Count }
            return w?Pick(n)
        }
    f () |> should equal "int"
    f () |> should equal "int"
    DlrCache.count () |> should equal (before + 1)

type Holder<'T>(w: obj) =
    member _.Pick(x: 'T) : string = dlr { return w?Pick(x) }
    member _.Pair(x: 'T) (y: 'U) : string = dlr { return w?Greet(string x, string y) }
    member _.Unused() : int = dlr { return w?Count }

[<Fact>]
let ``generic type and generic method members`` () =
    let h = Holder<int>(Widget())
    h.Pick 1 |> should equal "int"
    Holder<string>(Widget()).Pick "s" |> should equal "string"
    h.Pair 1 "b" |> should equal "1, b"
    h.Pair 1 2.5 |> should equal "1, 2.5"
    // Unused never mentions 'T, but it reads w, a constructor parameter, i.e. a field of
    // this: Holder<'T>, so its closure is generic in 'T: one site per instantiation, then stable.
    let before = DlrCache.count ()
    h.Unused() |> should equal 3
    Holder<string>(Widget()).Unused() |> should equal 3
    Holder<float>(Widget()).Unused() |> should equal 3
    DlrCache.count () |> should equal (before + 3)
    h.Unused() |> should equal 3
    Holder<string>(Widget()).Unused() |> should equal 3
    DlrCache.count () |> should equal (before + 3)
