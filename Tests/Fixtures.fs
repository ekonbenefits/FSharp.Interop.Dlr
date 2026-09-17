namespace Tests

open System
open System.Collections.Generic
open System.Dynamic
open System.Runtime.InteropServices
open FSharp.Interop.Dlr

/// Plain CLR target with overloads, so binder flags are observable.
[<ReflectedDefinition>]
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
    member _.BumpF(count: int, ?step: int) = count + defaultArg step 1
    member _.Wrap(?prefix: string, ?suffix: string) = (defaultArg prefix "<") + "x" + (defaultArg suffix ">")
    member this.TouchF(?times: int) = for _ in 1 .. defaultArg times 1 do this.Touch()
    member val Touched = 0 with get, set
    member this.Touch() = this.Touched <- this.Touched + 1
    member _.Item with get (i: int) = i * 10
    member _.Run(f: Func<int, int>) = f.Invoke 21
    static member Make() = Widget()
    member val Total = 10 with get, set
    member val Label = "a" with get, set
    member val Small: byte = 250uy with get, set
    member _.Default<'T>() : 'T = Unchecked.defaultof<'T>
    member _.TypeName<'T>() = typeof<'T>.Name
    member _.Pair<'A, 'B>(_: 'A, _: 'B) = sprintf "%s/%s" (typeof<'A>.Name) (typeof<'B>.Name)
    member _.Echo<'T>(x: 'T) : 'T = x
    member _.Narrow(_: byte) = "byte"
    member _.Narrow(_: int64) = "int64"
    member _.Kind(_: DayOfWeek) = "enum"
    member _.Kind(_: obj) = "obj"
    member _.Text(_: string) = "string"
    member _.Text(_: int) = "int"
    member val Ratio = 2.75 with get, set
    member private _.Secret = "hidden"
    member _.PeekSecretFromOutside(o: obj) : string =
        // A dlr block inside Widget itself: the binder context is Widget, so private members bind.
        dlr { return o?Secret }

/// A real CLR event, for the IsEvent branch of += / -=. Not [<ReflectedDefinition>]: the
/// [<CLIEvent>] accessor's stored quotation is one FSharp.Core cannot decode.
type Clicker() =
    let clicked = Event<int>()
    [<CLIEvent>]
    member _.Clicked = clicked.Publish
    member _.Raise(n: int) = clicked.Trigger(n)
/// A DynamicObject with a Count, for the polymorphic-site tests (Recorder logs; this one is quiet).
type Counter(n: int) =
    inherit DynamicObject()
    override _.TryGetMember(binder, result) =
        if binder.Name = "Count" then result <- box n; true else false

/// CLR members whose declared types say different things about what they hold.
type Holders() =
    member val AsObj: obj = box (fun (x: int) -> x * 3) with get, set
    member val AsDelegate: Func<int> = Func<int>(fun () -> 9) with get, set
    member val AsFunction: int -> int = (fun x -> x + 1) with get, set
    member _.Item with get (i: int) = i * 10

type IGreeter =
    abstract Greet: string -> string

/// F# interface implementations are always explicit: Greet exists only as IGreeter.Greet.
type Greeter() =
    member _.Name = "greeter"
    interface IGreeter with
        member _.Greet(who) = "hello " + who

/// A C#-style extension method on Widget: the binder never sees these, as in C#.
[<System.Runtime.CompilerServices.Extension>]
type WidgetExtensions =
    [<System.Runtime.CompilerServices.Extension>]
    static member Twice(w: Widget) = w.Count * 2

/// Records which DLR operations reached it.
type Recorder() =
    inherit DynamicObject()
    member val Log = ResizeArray<string>()
    override this.TrySetMember(binder, value) =
        this.Log.Add(sprintf "set %s=%O" binder.Name value)
        true
    override this.TryInvoke(_, args, result) =
        this.Log.Add(sprintf "invoke self(%d args)" args.Length)
        result <- box (String.Join("|", args))
        true
    override this.TryGetMember(binder, result) =
        this.Log.Add("get " + binder.Name)
        result <- (if binder.Name = "Self" then box this else box binder.Name)
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

/// Has a user-defined implicit conversion from int, for the Convert binder.
type Meters(value: int) =
    member _.Value = value
    static member op_Implicit(n: int) : Meters = Meters(n)

module Fixtures =
    let expando (pairs: (string * obj) list) =
        let e = ExpandoObject()
        let d = e :> IDictionary<string, obj>
        for k, v in pairs do d.[k] <- v
        e

/// Overrides the DynamicObject hooks the operators and conversions hit.
type Arith(value: int) =
    inherit DynamicObject()
    member _.Value = value
    override _.TryInvoke(_, args, result) =
        result <- box (sprintf "invoked with %d args" args.Length)
        true
    override _.TryBinaryOperation(binder, arg, result) =
        let other = match arg with :? Arith as a -> a.Value | o -> unbox<int> o
        match binder.Operation with
        | System.Linq.Expressions.ExpressionType.Add -> result <- box (Arith(value + other)); true
        | System.Linq.Expressions.ExpressionType.Multiply -> result <- box (Arith(value * other)); true
        | System.Linq.Expressions.ExpressionType.Equal -> result <- box (value = other); true
        | System.Linq.Expressions.ExpressionType.LessThan -> result <- box (value < other); true
        | _ -> false
    override _.TryUnaryOperation(binder, result) =
        match binder.Operation with
        | System.Linq.Expressions.ExpressionType.Negate -> result <- box (Arith(-value)); true
        | _ -> false
    override _.TryConvert(binder, result) =
        if binder.Type = typeof<int> then result <- box value; true
        elif binder.Type = typeof<string> then result <- box (string value); true
        else false
    override _.TryGetMember(binder, result) =
        // Only "Value" exists; anything else is a genuine miss.
        if binder.Name = "Value" then result <- box value; true else false

/// IDynamicMetaObjectProvider implemented directly, without DynamicObject.
type Bag() =
    let data = Dictionary<string, obj>()
    member _.Data = data
    interface IDynamicMetaObjectProvider with
        member this.GetMetaObject(expression) = BagMeta(expression, this) :> DynamicMetaObject

and BagMeta(expression, bag: Bag) =
    inherit DynamicMetaObject(expression, BindingRestrictions.Empty, bag)
    let self = System.Linq.Expressions.Expression.Convert(expression, typeof<Bag>)
    let restrictions () = BindingRestrictions.GetTypeRestriction(expression, typeof<Bag>)
    override _.BindGetMember(binder) =
        let call =
            System.Linq.Expressions.Expression.Call(
                typeof<BagMeta>.GetMethod("Get"), self, System.Linq.Expressions.Expression.Constant binder.Name)
        DynamicMetaObject(call, restrictions ())
    override _.BindSetMember(binder, value) =
        let call =
            System.Linq.Expressions.Expression.Call(
                typeof<BagMeta>.GetMethod("Set"), self, System.Linq.Expressions.Expression.Constant binder.Name,
                System.Linq.Expressions.Expression.Convert(value.Expression, typeof<obj>))
        DynamicMetaObject(call, restrictions ())
    static member Get(bag: Bag, name: string) : obj = bag.Data.[name]
    static member Set(bag: Bag, name: string, value: obj) : obj = bag.Data.[name] <- value; value
