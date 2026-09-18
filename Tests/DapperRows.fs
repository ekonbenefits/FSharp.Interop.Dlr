/// Dapper's dynamic rows (`DapperRow`, an IDynamicMetaObjectProvider that is also an
/// IDictionary<string, obj>) over an in-memory SQLite database: the second most common `dynamic`
/// after JObject, with SQLite's own typing (integers come back as int64, text as string, NULL as null).
[<ReflectedDefinition>]
module Tests.DapperRows

open System
open System.Collections.Generic
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Dapper
open Microsoft.Data.Sqlite
open Microsoft.CSharp.RuntimeBinder

let private openDb () =
    let c = new SqliteConnection("Data Source=:memory:")
    c.Open()
    c.Execute("create table widget (id integer primary key, name text, count integer, price real, note text, [display name] text)") |> ignore
    c.Execute("insert into widget (name, count, price, note, [display name]) values ('a', 3, 2.5, null, 'Widget A'), ('b', 10, 0.5, 'n', 'Widget B')") |> ignore
    c

[<Fact>]
let ``row columns read with SQLite's types, converting to the inferred ones`` () =
    use c = openDb ()
    let row = box (c.QueryFirst("select * from widget where name = 'a'"))
    (dlr { return row?name } : string) |> should equal "a"
    (dlr { return row?count } : int64) |> should equal 3L        // SQLite integer
    (dlr { return Dlr.cast<int> row?count } : int) |> should equal 3   // int64 -> int is a cast, as in C#
    (fun () -> (dlr { return row?count } : int) |> ignore) |> should throw typeof<RuntimeBinderException>   // no implicit narrowing
    (dlr { return row?price } : float) |> should equal 2.5
    let note: obj = dlr { return row?note }                      // NULL is null
    isNull note |> should equal true
    // An unknown column is not a binder error: DapperRow's TryGetMember answers null for any name.
    let nothere: obj = dlr { return row?nothere }
    isNull nothere |> should equal true

[<Fact>]
let ``a query's rows in a loop reuse one site; a column name with a space is a computed name`` () =
    use c = openDb ()
    let rows = c.Query("select * from widget order by id") |> List.ofSeq
    let acc = ResizeArray<string>()
    dlr {
        for r in rows do
            let o = box r
            acc.Add((o?name : string) + "=" + string (o?count : int64))
    }
    List.ofSeq acc |> should equal [ "a=3"; "b=10" ]
    let col = "display name"
    (dlr { return (?) (box rows.Head) col } : string) |> should equal "Widget A"

[<Fact>]
let ``rows are settable and dictionary-like too`` () =
    use c = openDb ()
    let r = c.QueryFirst("select * from widget where name = 'b'")
    let o = box r
    dlr { o?count <- 11L }                                       // DapperRow's TrySetMember
    (dlr { return o?count } : int64) |> should equal 11L
    (r :?> IDictionary<string, obj>).["count"] |> should equal (box 11L)
    // IDictionary is implemented explicitly on DapperRow, so `o?Keys` is not reachable (README:
    // explicitly implemented interface members); cast statically for those.
    (r :?> IDictionary<string, obj>).Keys.Count |> should equal 6

[<Fact>]
let ``one site serves a Dapper row and a JObject`` () =
    use c = openDb ()
    let targets: obj list = [ box (c.QueryFirst("select name from widget where id = 1")); box (Newtonsoft.Json.Linq.JObject.Parse """{ "name": "j" }""") ]
    let names = ResizeArray<string>()
    dlr {
        for t in targets do
            names.Add(t?name)
    }
    List.ofSeq names |> should equal [ "a"; "j" ]
