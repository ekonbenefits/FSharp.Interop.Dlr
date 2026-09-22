/// NLua: a dynamic API that is not a DLR one. A Lua table is `t.["name"]` and a function is
/// `f.Call(args)` — late-bound by string, with nothing for a binder to see. Twenty lines of
/// `DynamicObject` make them meta-objects, and every form below reads as it does on ClearScript or
/// IronPython. The pattern for any such API: answer the binder's hooks with the API's own calls.
/// Skipped where NLua's native Lua cannot load. (The adapter has out-parameters, so the attribute
/// goes on the facts, not the module.)
module Tests.NLua

open System.Dynamic
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open NLua

// --- the adapter ----------------------------------------------------------------------------

/// Lua values as dynamic objects.
module LuaDynamic =

    /// A table: its fields are members and indexes (an absent one is nil, as in Lua — never a miss);
    /// a member call is `t:method(args)`, the table as self.
    type Table(t: LuaTable) =
        inherit DynamicObject()
        override _.TryGetMember(binder, result) = result <- Value.Wrap t.[binder.Name]; true
        override _.TrySetMember(binder, value) = t.[binder.Name] <- value; true
        override _.TryGetIndex(_, indexes, result) = result <- Value.Wrap t.[indexes.[0]]; true
        override _.TrySetIndex(_, indexes, value) = t.[indexes.[0]] <- value; true
        override _.TryInvokeMember(binder, args, result) =
            match t.[binder.Name] with
            | :? LuaFunction as f -> result <- Value.Returned(f.Call(Array.append [| box t |] args)); true
            | _ -> false

    /// A function: applying it calls it.
    and Function(f: LuaFunction) =
        inherit DynamicObject()
        override _.TryInvoke(_, args, result) = result <- Value.Returned(f.Call args); true

    /// Values as they come out of Lua: tables and functions wrapped, everything else as NLua gives
    /// it (integers as `int64`); multiple returns are one value, or an array.
    and Value private () =
        static member Wrap(v: obj) : obj =
            match v with
            | :? LuaTable as t -> box (Table t)
            | :? LuaFunction as f -> box (Function f)
            | v -> v
        static member Returned(rs: obj[]) : obj =
            match rs with
            | [| one |] -> Value.Wrap one
            | many -> box (Array.map Value.Wrap many)

// --- the fixture ----------------------------------------------------------------------------

let private lua =
    lazy
        (try
            let lua = new Lua()
            lua.DoString("""
                widget = { name = "lua", count = 3, tags = { "a", "b" }, nested = { deep = 42 }, nothing = nil }
                function widget:greet(who) return "hi " .. who .. " from " .. self.name end
                function widget:add(a, b) return a + b end
                function makeAdder(n) return function (x) return x + n end end
                function two() return 1, 2 end
            """) |> ignore
            Some lua
         with _ -> None)

let private require () =
    match lua.Value with
    | Some l -> l
    | None -> raise (AnyUnit.IgnoreException "NLua's native Lua library not available")

let private global' (name: string) : obj = LuaDynamic.Value.Wrap (require ()).[name]

// --- the tests: each form, as on any other target --------------------------------------------

[<Fact; ReflectedDefinition>]
let ``table fields, nested tables, and typed conversion`` () =
    let w = global' "widget"
    let name: string = dlr { return w?name }
    let count: int64 = dlr { return w?count }                    // Lua integers are int64
    let asFloat: float = dlr { return w?count }
    let deep: int64 = dlr { return w?nested?deep }
    name |> should equal "lua"
    count |> should equal 3L
    asFloat |> should equal 3.0
    deep |> should equal 42L

[<Fact; ReflectedDefinition>]
let ``methods with self, functions as values, closures, multiple returns`` () =
    let w = global' "widget"
    let greeting: string = dlr { return w?greet("f#") }          // widget:greet — the table is self
    let sum: int64 = dlr { return w?add(2, 3) }
    greeting |> should equal "hi f# from lua"
    sum |> should equal 5L
    let makeAdder = global' "makeAdder"
    let add5: obj = dlr { return makeAdder |> Dlr.apply 5 }      // a Lua closure comes back callable
    let fifteen: int64 = dlr { return add5 |> Dlr.apply 10 }
    fifteen |> should equal 15L
    let two = global' "two"
    let pair: obj[] = dlr { return two |> Dlr.apply () }          // multiple returns
    pair |> should equal [| box 1L; box 2L |]

[<Fact; ReflectedDefinition>]
let ``arrays index from one; fields set, add, and nil crosses as null`` () =
    let w = global' "widget"
    let second: string = dlr { return w?tags |> Dlr.item 2L }
    second |> should equal "b"
    dlr { w?tags |> Dlr.setItem 1L "z" }
    let first: string = dlr { return w?tags |> Dlr.item 1L }
    first |> should equal "z"
    dlr { w?extra <- "new" }
    let extra: string = dlr { return w?extra }
    extra |> should equal "new"
    let nothing: obj = dlr { return w?nothing }                  // nil, and an absent field alike
    let absent: obj = dlr { return w?notThere }
    isNull nothing |> should equal true
    isNull absent |> should equal true

[<Fact; ReflectedDefinition>]
let ``one site serves a Lua table, a JObject and an Expando`` () =
    let targets: obj list = [ global' "widget"; box (Newtonsoft.Json.Linq.JObject.Parse """{ "name": "j" }"""); box (Fixtures.expando [ "name", box "x" ]) ]
    let names = ResizeArray<string>()
    dlr {
        for t in targets do
            names.Add(t?name)
    }
    List.ofSeq names |> should equal [ "lua"; "j"; "x" ]
