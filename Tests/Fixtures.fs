namespace Tests

open System
open System.Collections.Generic
open System.Dynamic
open System.Runtime.InteropServices

/// Plain CLR target with overloads, so binder flags are observable.
type Widget() =
    member val Name = "widget" with get, set
    member val Count = 3 with get, set
    member _.Describe() = "described"
    member _.Pick(_: int) = "int"
    member _.Pick(_: obj) = "obj"
    member _.Pick(_: string) = "string"
    member _.Add(a: int, b: int) = a + b
    member _.Greet(greeting: string, name: string) = greeting + ", " + name
    member _.Bump(count: int, [<Optional; DefaultParameterValue(1)>] step: int) = count + step
    member val Touched = 0 with get, set
    member this.Touch() = this.Touched <- this.Touched + 1
    member _.Item with get (i: int) = i * 10
    member _.Run(f: Func<int, int>) = f.Invoke 21

/// Records which DLR operations reached it.
type Recorder() =
    inherit DynamicObject()
    member val Log = ResizeArray<string>()
    override this.TryGetMember(binder, result) =
        this.Log.Add("get " + binder.Name)
        result <- box binder.Name
        true
    override this.TrySetMember(binder, value) =
        this.Log.Add(sprintf "set %s=%O" binder.Name value)
        true
    override this.TryInvokeMember(binder, args, result) =
        let names = binder.CallInfo.ArgumentNames |> String.concat ","
        this.Log.Add(sprintf "invoke %s(%d args; named %s)" binder.Name args.Length names)
        result <- box (String.Join("|", args))
        true
    override this.TryGetIndex(_, indexes, result) =
        this.Log.Add(sprintf "getIndex %s" (String.Join(",", indexes)))
        result <- box (indexes.Length)
        true
    override this.TrySetIndex(_, indexes, value) =
        this.Log.Add(sprintf "setIndex %s=%O" (String.Join(",", indexes)) value)
        true

module Fixtures =
    let expando (pairs: (string * obj) list) =
        let e = ExpandoObject()
        let d = e :> IDictionary<string, obj>
        for k, v in pairs do d.[k] <- v
        e
