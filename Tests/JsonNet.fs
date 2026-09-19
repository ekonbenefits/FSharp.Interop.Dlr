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
    let o: obj = JObject.Parse json
    let name: string = dlr { return o?name }
    let count: int = dlr { return o?count }
    let wide: int64 = dlr { return o?count }                     // JValue.TryConvert widens
    let price: float = dlr { return o?price }
    let active: bool = dlr { return o?active }
    let owner: string = dlr { return o?owner?name }              // chained: owner is a JObject
    name |> should equal "widget"
    count |> should equal 3
    wide |> should equal 3L
    price |> should equal 2.5
    active |> should equal true
    owner |> should equal "jay"

[<Fact>]
let ``arrays index and iterate`` () =
    let o: obj = JObject.Parse json
    let tag: string = dlr { return o?tags |> Dlr.item 1 }
    let id: int = dlr { return o?owner?ids |> Dlr.item 2 }
    let tagCount: int = dlr { return o?tags?Count }
    tag |> should equal "b"
    id |> should equal 3
    tagCount |> should equal 2
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
    let o: obj = j
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
    let o: obj = JObject.Parse json
    let read (field: string) : string = dlr { return (?) o field }
    read "name" |> should equal "widget"
    let missing: obj = dlr { return o?missing }                  // a JSON null is a JValue of null
    (missing :?> JValue).Type |> should equal JTokenType.Null
    let nothere: obj = dlr { return o?nothere }                  // JObject answers null for an absent member
    isNull nothere |> should equal true
    (fun () -> (dlr { return o?nothere } : int) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``JObject methods bind like any CLR method`` () =
    let o: obj = JObject.Parse json
    let hasName: bool = dlr { return o?ContainsKey("name") }
    hasName |> should equal true
    let compact: string = dlr { return o?ToString(Newtonsoft.Json.Formatting.None) }
    compact.StartsWith "{\"name\"" |> should equal true
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
