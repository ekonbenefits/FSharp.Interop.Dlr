/// Newtonsoft.Json's JObject/JArray/JValue: the most common `dynamic` in the wild. JToken is an
/// IDynamicMetaObjectProvider whose meta-object answers GetMember, SetMember, GetIndex,
/// SetIndex and Convert itself.
[<ReflectedDefinition>]
module Tests.JsonNet

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Newtonsoft.Json.Linq
open Microsoft.CSharp.RuntimeBinder

let private json =
    """{ "name": "widget", "count": 3, "price": 2.5, "active": true, "tags": ["a", "b"],
         "owner": { "name": "jay", "ids": [1, 2, 3] }, "missing": null }"""

[<Fact>]
let ``reading members converts JValues to the inferred types`` () =
    let o = box (JObject.Parse json)
    (dlr { return o?name } : string) |> should equal "widget"
    (dlr { return o?count } : int) |> should equal 3
    (dlr { return o?count } : int64) |> should equal 3L        // JValue.TryConvert widens
    (dlr { return o?price } : float) |> should equal 2.5
    (dlr { return o?active } : bool) |> should equal true
    (dlr { return o?owner?name } : string) |> should equal "jay"   // chained: owner is a JObject

[<Fact>]
let ``arrays index and iterate`` () =
    let o = box (JObject.Parse json)
    (dlr { return o?tags |> Dlr.item 1 } : string) |> should equal "b"
    (dlr { return o?owner?ids |> Dlr.item 2 } : int) |> should equal 3
    (dlr { return o?tags?Count } : int) |> should equal 2
    let sum =
        dlr {
            let mutable total = 0
            for id in (o?owner?ids : JArray) do
                total <- total + (box id |> Dlr.implicit : int)
            return total
        }
    sum |> should equal 6

[<Fact>]
let ``members set and add, values write back to the tree`` () =
    let j = JObject.Parse json
    let o = box j
    dlr { o?count <- 10 }
    dlr { o?owner?name <- "someone" }
    dlr { o?extra <- "new" }                                     // a member that was not there
    j.["count"].Value<int>() |> should equal 10
    j.["owner"].["name"].Value<string>() |> should equal "someone"
    j.["extra"].Value<string>() |> should equal "new"
    dlr { o |> Dlr.addAssign "count" 5 }                         // read-modify-write through the tree
    j.["count"].Value<int>() |> should equal 15

[<Fact>]
let ``computed names, null members and misses`` () =
    let o = box (JObject.Parse json)
    let read (field: string) : string = dlr { return (?) o field }
    read "name" |> should equal "widget"
    let missing: obj = dlr { return o?missing }                  // a JSON null is a JValue of null
    (missing :?> JValue).Type |> should equal JTokenType.Null
    let nothere: obj = dlr { return o?nothere }                  // JObject answers null for an absent member
    isNull nothere |> should equal true
    (fun () -> (dlr { return o?nothere } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``JObject methods bind like any CLR method`` () =
    let o = box (JObject.Parse json)
    (dlr { return o?ContainsKey("name") } : bool) |> should equal true
    (dlr { return o?ToString(Newtonsoft.Json.Formatting.None) } : string).StartsWith "{\"name\"" |> should equal true
    let deep: string = dlr { return (o?SelectToken("owner.ids[1]") : JToken).ToString() }
    deep |> should equal "2"

[<Fact>]
let ``one site serves JObjects, Expandos and CLR objects alike`` () =
    let targets: obj list = [ box (JObject.Parse json); box (Fixtures.expando [ "name", box "expando" ]); box (Widget()) ]
    let names = ResizeArray<string>()
    dlr {
        for t in targets do
            try names.Add(t?name : string)
            with :? RuntimeBinderException -> names.Add "?"      // Widget's is `Name`: a CLR miss is C#'s error
    }
    List.ofSeq names |> should equal [ "widget"; "expando"; "?" ]
