[<ReflectedDefinition>]
module Tests.ComputedNames

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

[<Fact>]
let ``get member by a computed name`` () =
    let w = box (Widget())
    let read (name: string) : obj = dlr { return (?) w name }
    read "Count" |> should equal (box 3)
    read "Name" |> should equal (box "widget")

[<Fact>]
let ``computed name converts to the inferred type`` () =
    let w = box (Widget())
    let count (name: string) : int = dlr { return (?) w name }
    count "Count" |> should equal 3

[<Fact>]
let ``invoke by a computed name with typed and named args`` () =
    let w = box (Widget())
    let call (name: string) (n: int) : string = dlr { return ((?) w name) (n) }
    call "Pick" 1 |> should equal "int"
    let greet (name: string) : string = dlr { return ((?) w name) ("Hi", Dlr.named {| name = "Jay" |}) }
    greet "Greet" |> should equal "Hi, Jay"

[<Fact>]
let ``set member by a computed name`` () =
    let w = Widget()
    let o = box w
    let set (name: string) (v: int) = dlr { (?<-) o name v }
    set "Count" 11
    w.Count |> should equal 11

[<Fact>]
let ``computed names on a DynamicObject and Expando`` () =
    let r = Recorder()
    let o = box r
    let get (name: string) : string = dlr { return (?) o name }
    get "Alpha" |> should equal "Alpha"
    get "Beta" |> should equal "Beta"
    List.ofSeq r.Log |> should equal [ "get Alpha"; "get Beta" ]
    let e = box (Fixtures.expando [ "x", box 1; "y", box 2 ])
    let sum: int = dlr { return (?) e "x" + ((?) e (string 'y') : int) }
    sum |> should equal 3

[<Fact>]
let ``each distinct name compiles once and the site stays one cache entry`` () =
    let w = box (Widget())
    let read (name: string) : obj = dlr { return (?) w name }
    let before = DlrCache.count ()
    for _ in 1 .. 100 do
        read "Count" |> ignore
        read "Name" |> ignore
    DlrCache.count () |> should equal (before + 1)

[<Fact>]
let ``computed name that does not exist raises RuntimeBinderException`` () =
    let w = box (Widget())
    let read (name: string) : obj = dlr { return (?) w name }
    (fun () -> read "Nope" |> ignore) |> should throw typeof<RuntimeBinderException>

let private oneSiteTemplate (name: string) =
    let binder = Microsoft.CSharp.RuntimeBinder.Binder.GetMember(Microsoft.CSharp.RuntimeBinder.CSharpBinderFlags.None, name, typeof<obj>, [ Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo.Create(Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfoFlags.None, null) ])
    let site = System.Runtime.CompilerServices.CallSite<System.Func<System.Runtime.CompilerServices.CallSite, obj, obj>>.Create binder
    FSharp.Quotations.Expr.Value(site, site.GetType())

[<Fact>]
let ``a site's name cache is bounded`` () =
    // Direct: the cache is a constant inside the compiled block, so exercise the type itself with
    // a template that makes one site per key, as the translator's do.
    let cache = SiteCache<string>(oneSiteTemplate)
    let first = cache.Get "n0"
    obj.ReferenceEquals(cache.Get "n0", first) |> should equal true      // a hit returns the same sites
    first.Length |> should equal 1
    for i in 1 .. 1000 do cache.Get(sprintf "n%d" i) |> ignore
    (cache.Count <= SiteCache<string>.Capacity) |> should equal true
    cache.Get "n0" |> ignore                                             // still works after clearing
    (cache.Count >= 1) |> should equal true

// Needs real threads: on single-threaded wasm Parallel.For runs sequentially and would prove
// nothing, so there the test is skipped rather than passed (AnyUnit has no threading capability yet).
[<Fact>]
let ``concurrent misses cannot push a site's name cache past its bound`` () =
    if System.Environment.ProcessorCount < 2 then raise (AnyUnit.IgnoreException "needs more than one thread")
    let cache = SiteCache<string>(oneSiteTemplate)
    System.Threading.Tasks.Parallel.For(0, 4000, fun i -> cache.Get(sprintf "p%d" i) |> ignore) |> ignore
    (cache.Count <= SiteCache<string>.Capacity) |> should equal true

[<Fact>]
let ``a thousand distinct names through one site`` () =
    let w = box (Widget())
    let hits = ResizeArray<int>()
    let names = [ for i in 1 .. 1000 -> if i % 2 = 0 then "Count" else "Name" + string i ]
    dlr {
        for n in names do
            try hits.Add((?) w n)
            with :? RuntimeBinderException -> ()
    }
    List.ofSeq hits |> should equal [ for _ in 1 .. 500 -> 3 ]

[<Fact>]
let ``a void method by a computed name is a discarded call`` () =
    let w = Widget()
    let o = box w
    let m = "Touch"
    dlr { (?) o m () }
    w.Touched |> should equal 1
    let t = typeof<int>
    dlr { o?TouchT(Dlr.typeArgsOf [ t ]) }
    w.Touched |> should equal 2
