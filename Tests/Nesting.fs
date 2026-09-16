[<ReflectedDefinition>]
module Tests.Nesting

open FSharp.Interop.Dlr
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit

[<Fact>]
let ``nested dlr blocks`` () =
    let w = box (Widget())
    let n: int = dlr { return (dlr { return w?Count } : int) + w?Count }
    n |> should equal 6

[<Fact>]
let ``dlr inside task`` () =
    let w = box (Widget())
    let t = task {
        do! System.Threading.Tasks.Task.Yield()
        return (dlr { return w?Count } : int)
    }
    t.Result |> should equal 3

[<Fact>]
let ``dlr inside async`` () =
    let w = box (Widget())
    let a = async {
        do! Async.Sleep 1
        return (dlr { return w?Count } : int)
    }
    Async.RunSynchronously a |> should equal 3

[<Fact>]
let ``nested dlr blocks on separate lines share the outer site`` () =
    let w = box (Widget())
    let before = DlrCache.count ()
    let f () : string =
        dlr {
            let n: int = dlr { return w?Count }
            return w?Pick(n)
        }
    f () |> should equal "int"
    f () |> should equal "int"
    DlrCache.count () |> should equal (before + 1)
