/// NLua: a dynamic API that is not a DLR one. A Lua table is `t.["name"]` and a function is
/// `f.Call(args)` — late-bound by string, with nothing for a binder to see. Twenty lines of
/// `DynamicObject` make them meta-objects, and every form below reads as it does on ClearScript or
/// IronPython. The pattern for any such API: answer the binder's hooks with the API's own calls,
/// wrapping values on the way out and unwrapping them on the way in; an F# function handed to
/// Lua arrives as a delegate, which Lua can call, by the seam's rule for meta-object targets.
/// Skipped where NLua's native Lua cannot load. (The adapter has out-parameters, so the attribute goes on
/// the facts, not the module.)
module Tests.NLua

open System.Dynamic
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open NLua

// --- the adapter ----------------------------------------------------------------------------

/// Lua values as dynamic objects.
module LuaDynamic =

    /// A table: its fields are members and indexes (an absent one is nil, as in Lua — never a miss).
    /// A member call `w?f(args)` is `t:f(args)`, the table as self; a plain function field is
    /// `t.f(args)` in Lua, so call it as a value: `w?f |> Dlr.apply args`. A call on a field that is
    /// not a function is left to C#'s binder, whose error is about the value.
    type Table(t: LuaTable) =
        inherit DynamicObject()
        member _.Raw = t
        override _.TryGetMember(binder, result) = result <- Value.Wrap t.[binder.Name]; true
        override _.TrySetMember(binder, value) = t.[binder.Name] <- Value.Unwrap value; true
        override _.TryGetIndex(_, indexes, result) = result <- Value.Wrap t.[Value.Unwrap indexes.[0]]; true
        override _.TrySetIndex(_, indexes, value) = t.[Value.Unwrap indexes.[0]] <- Value.Unwrap value; true
        override _.TryInvokeMember(binder, args, result) =
            match t.[binder.Name] with
            | :? LuaFunction as f -> result <- Value.Returned(f.Call(Array.append [| box t |] (Array.map Value.Unwrap args))); true
            | _ -> false

    /// A function: applying it calls it.
    and Function(f: LuaFunction) =
        inherit DynamicObject()
        member _.Raw = f
        override _.TryInvoke(_, args, result) = result <- Value.Returned(f.Call(Array.map Value.Unwrap args)); true

    /// Values crossing: out of Lua, tables and functions wrapped and everything else as NLua gives
    /// it (integers as `int64`), multiple returns one value, an array, or null for none; into Lua,
    /// a wrapped table or function is the table or function again, not userdata.
    and Value private () =
        static member Wrap(v: obj) : obj =
            match v with
            | :? LuaTable as t -> box (Table t)
            | :? LuaFunction as f -> box (Function f)
            | v -> v
        static member Unwrap(v: obj) : obj =
            match v with
            | :? Table as t -> box t.Raw
            | :? Function as f -> box f.Raw
            | v -> v
        static member Returned(rs: obj[]) : obj =
            match rs with
            | [||] -> null
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
                widget.plain = function (a) return a end
                function widget:size(t) return #t end
                function widget:each(f) for _, v in ipairs(self.tags) do f(v) end end
                function widget:map(f) return f(self.count) end
                function makeAdder(n) return function (x) return x + n end end
                function two() return 1, 2 end
                function none() end
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
    let plain: int64 = dlr { return w?plain |> Dlr.apply 1 }     // widget.plain — no self: call it as a value
    plain |> should equal 1L
    let size: int64 = dlr { return w?size(w?tags) }              // a table handed back to Lua is a table again
    size |> should equal 2L
    let makeAdder = global' "makeAdder"
    let add5: obj = dlr { return makeAdder |> Dlr.apply 5 }      // a Lua closure comes back callable
    let fifteen: int64 = dlr { return add5 |> Dlr.apply 10 }
    fifteen |> should equal 15L
    let two = global' "two"
    let pair: obj[] = dlr { return two |> Dlr.apply () }          // multiple returns
    pair |> should equal [| box 1L; box 2L |]
    let none = global' "none"
    let nothing: obj = dlr { return none |> Dlr.apply () }        // no returns: null, as a nil
    isNull nothing |> should equal true

[<Fact; ReflectedDefinition>]
let ``an F# function passed to Lua is called from Lua`` () =
    let w = global' "widget"
    let seen = ResizeArray<string>()
    dlr { w?each(fun (tag: string) -> seen.Add tag) }             // string -> unit: an Action<string> Lua can call
    List.ofSeq seen |> should equal [ "a"; "b" ]
    let doubled: int64 = dlr { return w?map(fun (n: int64) -> n * 2L) }
    doubled |> should equal 6L
    let literal = ResizeArray<string>()
    dlr { w?each(System.Action<string>(fun tag -> literal.Add tag)) }     // a delegate literal: NLua reads its .Method, which names the real parameters
    List.ofSeq literal |> should equal [ "a"; "b" ]

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
