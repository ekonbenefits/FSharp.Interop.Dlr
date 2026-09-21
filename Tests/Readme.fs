/// The README's first example, verbatim, so it cannot drift from what the library does.
[<ReflectedDefinition>]
module Tests.Readme

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Newtonsoft.Json.Linq

/// The pricing plugin the host loaded: any object with these members.
type Pricing() =
    member _.Total(lines: JArray, currency: string) =
        let sum = lines |> Seq.sumBy (fun l -> l.Value<decimal> "price")
        if currency = "EUR" then sum * 0.9m else sum
    member _.Discount(customer: string, sum: decimal) = if customer = "Ada" then sum / 10m else 0m

// --- README, "invoice" ------------------------------------------------------------------------

let invoice (order: obj) (pricing: obj) =
    let customer: string = dlr { return order?customer?name }
    let city: string = dlr { return order?customer?address?city }
    let first: decimal = dlr { return order?lines |> Dlr.item 0 |> Dlr.get "price" }
    let total: decimal = dlr { return pricing?Total(order?lines, Dlr.named {| currency = "EUR" |}) }
    dlr { order?status <- "invoiced" }
    let discount: decimal =
        dlr {
            let mutable sum = 0m
            for line in (order?lines : JArray) do sum <- sum + (line?price : decimal)
            return pricing?Discount(customer, sum)
        }
    customer, city, first, total, discount

let depth (root: obj) : int =
    dlr {
        let rec depth (node: obj) : int = if isNull node then 0 else 1 + depth node?child
        return depth root
    }

// --- the fixture and the assertions -----------------------------------------------------------

let private orderJson = """{ "customer": { "name": "Ada", "address": { "city": "London" } },
                            "lines": [ { "price": 10.0 }, { "price": 30.0 } ], "status": "open" }"""

[<Fact>]
let ``the README's invoice example runs as written`` () =
    let order = JObject.Parse orderJson
    let customer, city, first, total, discount = invoice order (Pricing())
    customer |> should equal "Ada"
    city |> should equal "London"
    first |> should equal 10m
    total |> should equal 36m
    discount |> should equal 4m
    order.["status"].Value<string>() |> should equal "invoiced"

[<Fact>]
let ``the README's depth example walks a JSON tree`` () =
    depth (JObject.Parse """{ "child": { "child": { "child": {} } } }""") |> should equal 4
    depth (JObject.Parse "{}") |> should equal 1
