/// ClearScript's V8: JavaScript objects, arrays and functions as dynamic. JS objects are
/// ScriptObjects (IDynamicMetaObjectProvider) — properties, index by number or name, functions
/// callable as members or as values — and JS's `undefined` and `null` both cross over.
/// Skipped when the native V8 cannot load.
[<ReflectedDefinition>]
module Tests.ClearScriptV8

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.ClearScript
open Microsoft.ClearScript.V8
open Microsoft.ClearScript.JavaScript
open Microsoft.CSharp.RuntimeBinder

let private engine =
    lazy
        (try
            let e = new V8ScriptEngine()
            e.Execute("""
                var widget = { name: "js", count: 3, tags: ["a", "b"], nested: { deep: 42 }, nothing: null,
                               add: function (a, b) { return a + b; },
                               greet: function (who) { return "hi " + who + " from " + this.name; } };
                var makeAdder = n => x => x + n;
                var numbers = [10, 20, 30];
                class Point { constructor(x, y) { this.x = x; this.y = y; } scaled(k = 2) { return new Point(this.x * k, this.y * k); } }
                var api = { fetchUser: async (id) => ({ id, name: "ada" }) };
            """)
            Some e
         with _ -> None)

let private require () =
    match engine.Value with
    | Some e -> e
    | None -> raise (AnyUnit.IgnoreException "ClearScript V8 native library not available")

[<Fact>]
let ``JS object properties, nested objects, and typed conversion`` () =
    let e = require ()
    let w = e.Evaluate "widget"
    let name: string = dlr { return w?name }
    let count: int = dlr { return w?count }
    let asFloat: float = dlr { return w?count }
    let deep: int = dlr { return w?nested?deep }
    name |> should equal "js"
    count |> should equal 3
    asFloat |> should equal 3.0
    deep |> should equal 42

[<Fact>]
let ``JS functions: as members (this bound), as values through Dlr.apply, closures, classes`` () =
    let e = require ()
    let w = e.Evaluate "widget"
    let script: obj = e.Script                                                 // the global object, a ScriptObject
    let sum: int = dlr { return w?add(2, 3) }
    let joined: string = dlr { return w?add("a", "b") }                        // JS duck typing at the same site
    let greeting: string = dlr { return w?greet("f#") }                        // `this` is the object
    sum |> should equal 5
    joined |> should equal "ab"
    greeting |> should equal "hi f# from js"
    let add5: obj = dlr { return script?makeAdder(5) }                         // a JS closure comes back callable
    let added: int = dlr { return add5 |> Dlr.apply 10 }
    added |> should equal 15
    // A JS class cannot be called without `new` (V8 refuses; ClearScript reports "Method or
    // property not found"), and C# `dynamic` has no `new` for script objects either: construct
    // through a JS factory or Evaluate, then use the instance dynamically.
    (fun () -> (dlr { return script?Point(1, 2) } : obj) |> ignore) |> should throw typeof<ScriptEngineException>
    let p = e.Evaluate "new Point(1, 2)"
    let scaledY: int = dlr { return p?scaled(3)?y }
    let defaultX: int = dlr { return p?scaled()?x }                            // default parameter
    scaledY |> should equal 6
    defaultX |> should equal 2

[<Fact>]
let ``JS arrays index by number, expose length, and iterate`` () =
    let e = require ()
    let w = e.Evaluate "widget"
    let tag: string = dlr { return w?tags |> Dlr.item 1 }
    let tagCount: int = dlr { return w?tags?length }
    tag |> should equal "b"
    tagCount |> should equal 2
    let numbers = e.Evaluate "numbers"
    let total: int =
        dlr {
            let mutable s = 0
            let n: int = numbers?length
            for i in 0 .. n - 1 do
                s <- s + (numbers |> Dlr.item i : int)
            return s
        }
    total |> should equal 60
    dlr { numbers |> Dlr.setItem 0 100 }
    let first: int = dlr { return numbers |> Dlr.item 0 }
    first |> should equal 100

[<Fact>]
let ``sets add properties; undefined and null both cross over`` () =
    let e = require ()
    let w = e.Evaluate "widget"
    dlr { w?extra <- "new" }
    let extra: string = dlr { return w?extra }
    extra |> should equal "new"
    e.Evaluate("widget.extra") |> should equal (box "new")
    let nothing: obj = dlr { return w?nothing }                                // JS null
    isNull nothing |> should equal true
    let missing: obj = dlr { return w?notThere }                               // JS undefined: ClearScript's Undefined value
    (missing :? Undefined) |> should equal true
    (fun () -> (dlr { return w?notThere } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``a JS promise: the call is dynamic, the await is ClearScript's ToTask, the result dynamic again`` () =
    // A promise has no GetAwaiter; ClearScript's bridge is an extension method, invisible to any
    // dynamic binder (C#'s too), so it is called statically between two blocks.
    let e = require ()
    let api = e.Evaluate "api"
    let t =
        task {
            let promise: obj = dlr { return api?fetchUser(7) }
            let! user = JavaScriptExtensions.ToTask promise
            let name: string = dlr { return user?name }
            let id: int = dlr { return user?id }
            return name, id
        }
    t.Result |> should equal ("ada", 7)

[<Fact>]
let ``one site serves a JS object, a JObject and an Expando`` () =
    let e = require ()
    let targets: obj list = [ e.Evaluate "widget"; box (Newtonsoft.Json.Linq.JObject.Parse """{ "name": "j" }"""); box (Fixtures.expando [ "name", box "x" ]) ]
    let names = ResizeArray<string>()
    dlr {
        for t in targets do
            names.Add(t?name)
    }
    List.ofSeq names |> should equal [ "js"; "j"; "x" ]
