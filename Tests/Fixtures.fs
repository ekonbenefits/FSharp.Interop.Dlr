namespace Tests

open System
open System.Collections.Generic
open System.Dynamic
open System.Threading.Tasks
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
    member _.Pick(_: unit, _: int) = "unit"
    member _.Add(a: int, b: int) = a + b
    member _.Greet(greeting: string, name: string) = greeting + ", " + name
    member _.Bump(count: int, [<Optional; DefaultParameterValue(1)>] step: int) = count + step
    member _.BumpF(count: int, ?step: int) = count + defaultArg step 1
    member _.Wrap(?prefix: string, ?suffix: string) = (defaultArg prefix "<") + "x" + (defaultArg suffix ">")
    member this.TouchT<'T>() = this.Touch()
    member this.TouchF(?times: int) = for _ in 1 .. defaultArg times 1 do this.Touch()
    member _.WidenF(n: int64, ?scale: int) = n * int64 (defaultArg scale 1)
    member _.LabelF(s: string, ?tag: string) = (if isNull s then "null" else s) + defaultArg tag ""
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
    member _.FiveNames<'A, 'B, 'C, 'D, 'E>() = String.Join("/", [| typeof<'A>.Name; typeof<'B>.Name; typeof<'C>.Name; typeof<'D>.Name; typeof<'E>.Name |])
    member _.Echo<'T>(x: 'T) : 'T = x
    member _.Narrow(_: byte) = "byte"
    member _.Narrow(_: int64) = "int64"
    member _.Kind(_: DayOfWeek) = "enum"
    member _.Kind(_: obj) = "obj"
    member _.Text(_: string) = "string"
    member _.Sum6(a: int, b: int, c: int, d: int, e: int, f: int) = a + b + c + d + e + f
    member _.SumAll([<ParamArray>] xs: int[]) = Array.sum xs
    member _.ThrowAll([<ParamArray>] xs: int[]) : int = raise (InvalidOperationException(sprintf "%d" xs.Length))
    member this.Touch15(a: int, b: int, c: int, d: int, e: int, f: int, g: int, h: int, i: int, j: int, k: int, l: int, m: int, n: int, o: int) =
        this.Touched <- this.Touched + a + b + c + d + e + f + g + h + i + j + k + l + m + n + o
    member _.Text(_: int) = "int"
    member val Ratio = 2.75 with get, set
    member private _.Secret = "hidden"
    member _.PeekSecretFromOutside(o: obj) : string =
        // A dlr block inside Widget itself: the binder context is Widget, so private members bind.
        dlr { return o?Secret }

/// Asynchronous members: a block returns what they return, converted to the awaitable type named.
type Service() =
    member _.GetAsync(x: int) : Task<int> = Task.FromResult(x * 2)
    member _.RunAsync() : Task = Task.CompletedTask
    member _.GetValueTask(x: int) : ValueTask<int> = ValueTask<int>(x + 1)
    member _.FetchAsync(x: int) : Async<int> = async { return x + 100 }

/// A mutable struct: through a dynamic operation it is a boxed copy, as through C# dynamic.
[<Struct>]
type Tally =
    val mutable Count: int
    member this.Bump() = this.Count <- this.Count + 1

/// Parameters no quotation can hold, so not [<ReflectedDefinition>]: byref cannot cross a
/// dynamic operation; a Span is reached from an array through C#'s implicit conversion.
type Unquotable() =
    member _.SpanLength(s: Span<int>) = s.Length
    member _.ByRef(x: byref<int>) = x <- x + 1

/// Internal delegates with byref parameters (F# compiles their Invoke internal), built here: no
/// quotation can hold a byref lambda. On .NET Framework C#'s Invoke binder cannot invoke them.
type internal InternalTryHalf = delegate of int * outref<int> -> bool
type internal InternalBump = delegate of byref<int> * int -> unit
type internal InternalByRefs() =
    static member TryHalf = InternalTryHalf(fun n half -> half <- n / 2; n % 2 = 0)
    static member Bump = InternalBump(fun x by -> x <- x + by)

/// A real CLR event, for the IsEvent branch of += / -=. Not [<ReflectedDefinition>]: the
/// [<CLIEvent>] accessor's stored quotation is one FSharp.Core cannot decode.
type Clicker() =
    let clicked = Event<int>()
    [<CLIEvent>]
    member _.Clicked = clicked.Publish
    member _.Raise(n: int) = clicked.Trigger(n)
/// An event of an internal delegate type, whose Invoke and constructor F# compiles internal (#150).
type internal InternalNotify = delegate of obj * int -> unit
type InternalClicker() =
    let changed = Event<InternalNotify, int>()
    [<CLIEvent>]
    member internal _.Changed = changed.Publish
    member _.Raise(n: int) = changed.Trigger(null, n)
/// A DynamicObject with a Count, for the polymorphic-site tests (Recorder logs; this one is quiet).
type Counter(n: int) =
    inherit DynamicObject()
    override _.TryGetMember(binder, result) =
        if binder.Name = "Count" then result <- box n; true else false

/// Members that throw, and F# function parameters past the typed adapters' five, for exceptions
/// through the run-time-built functions (read past five, delegate to function past five).
type Throwers() =
    member _.Five(a: int, b: int, c: int, d: int, e: int) : int = ignore (a, b, c, d, e); raise (InvalidOperationException "boom")
    member _.Six(a: int, b: int, c: int, d: int, e: int, f: int) : int = ignore (a, b, c, d, e, f); raise (InvalidOperationException "boom")
    member _.ApplyCurried6(f: int -> int -> int -> int -> int -> int -> int) = f 1 2 3 4 5 6
    member _.ApplyTupled6(f: int * int * int * int * int * int -> int) = f (1, 2, 3, 4, 5, 6)

/// CLR members whose declared types say different things about what they hold.
[<ReflectedDefinition>]
type Holders() =
    member val AsObj: obj = box (fun (x: int) -> x * 3) with get, set
    member val AsDelegate: Func<int> = Func<int>(fun () -> 9) with get, set
    member val AsFunction: int -> int = (fun x -> x + 1) with get, set
    member val Label: string -> string = (fun s -> if isNull s then "null" else s) with get
    member _.Item with get (i: int) = i * 10
    /// A void method, for a unit-valued argument expression that is not a closure call.
    static member Tick(log: ResizeArray<string>) = log.Add "tick"
    /// F# private: IL internal, reachable from this assembly's context, as the binder allows.
    member private _.Hidden = fun (x: int) -> x - 1
    member private _.BumpHidden(count: int, ?step: int) = count + defaultArg step 100
    member _.Reveal(o: obj) : int = dlr { return o?Hidden(10) }
    /// Protected: reachable from a derived type's context.
    abstract Family: int -> int
    default _.Family(x) = x * 2
    member _.Overloaded(s: string, ?tag: string) = "string:" + s + defaultArg tag ""
    member _.Overloaded(o: obj, ?tag: string) = "obj:" + string o + defaultArg tag ""
    member _.Wide = fun (x: int64) -> x + 1L
    member _.WideTupled = fun (x: int64, y: int64) -> x + y
    member _.WideCurried = fun (x: int64) (y: int64) -> x + y
    member _.Five = fun (a: int) (b: int) (c: int) (d: int) (e: int) -> a + b + c + d + e
    member _.Six = fun (a: int) (b: int) (c: int) (d: int) (e: int) (f: int) -> a * b * c * d * e * f
    member _.SixTupled = fun (a: int, b: int, c: int, d: int, e: int, f: int) -> a + b + c + d + e + f
    member _.EightTupled = fun (a: int, b: int, c: int, d: int, e: int, f: int, g: int, h: int) -> a + b + c + d + e + f + g + h
    member _.EightStruct = fun (struct (a: int, b: int, c: int, d: int, e: int, f: int, g: int, h: int)) -> a + b + c + d + e + f + g + h
    member _.SixteenTupled = fun (a: int, b: int, c: int, d: int, e: int, f: int, g: int, h: int, i: int, j: int, k: int, l: int, m: int, n: int, o: int, p: int) -> a + b + c + d + e + f + g + h + i + j + k + l + m + n + o + p
    member _.Eight = fun (a: int) (b: int) (c: int) (d: int) (e: int) (f: int) (g: int) (h: int) -> a + b + c + d + e + f + g + h
    member _.RevealOptional(o: obj) : int = dlr { return o?BumpHidden(1) }

type Derived() =
    inherit Holders()
    override _.Family(x) = x * 3
    [<ReflectedDefinition>]
    member this.CallFamily(o: obj) : int = dlr { return o?Family(5) }

type IGreeter =
    abstract Greet: string -> string

/// F# interface implementations are always explicit: Greet exists only as IGreeter.Greet.
type Greeter() =
    member _.Name = "greeter"
    interface IGreeter with
        member _.Greet(who) = "hello " + who

/// A C#-style extension method on Widget: the binder never sees these, as in C#.
[<System.Runtime.CompilerServices.Extension>]
/// Overloads on a base/derived pair, to tell static-type binding from runtime-type binding.
type Classifier() =
    member _.Kind(_: Holders) = "holders"
    member _.Kind(_: Derived) = "derived"
    member _.Kind(_: obj) = "obj"

type WidgetExtensions =
    [<System.Runtime.CompilerServices.Extension>]
    static member Twice(w: Widget) = w.Count * 2

/// A dynamic object that calls what it is handed: a script host's shape, for the rule that gives a
/// meta-object an F# function as the delegate of its signature.
type Caller() =
    inherit DynamicObject()
    member val Handler: Delegate = null with get, set
    override _.TryInvokeMember(binder, args, result) =
        match binder.Name, args with
        | "run", [| :? Delegate as d |] -> result <- d.DynamicInvoke(box 20); true
        | "run", [| other |] -> result <- box ("not a delegate: " + other.GetType().Name); true
        | "twice", [| :? Delegate as d; x |] -> result <- d.DynamicInvoke(d.DynamicInvoke x); true
        | "kinds", args -> result <- box (args |> Array.map (fun a -> if a :? Delegate then "delegate" else a.GetType().Name) |> String.concat ","); true
        | _ -> false
    override this.TrySetMember(binder, value) =
        match binder.Name, value with
        | "onEvent", (:? Delegate as d) -> this.Handler <- d; true
        | _ -> false
    override this.TrySetIndex(_, _, value) =
        match value with
        | :? Delegate as d -> this.Handler <- d; true
        | _ -> false

/// A COM event's shape anywhere: `Changed` reads as a bound event that takes `+=` / `-=` (a
/// BinaryOperation AddAssign / SubtractAssign) of a delegate only, as COM's BoundDispEvent does,
/// and the compound assignment's write-back is accepted and ignored.
type EventHost() =
    inherit DynamicObject()
    let handlers = ResizeArray<Delegate>()
    member _.Raise(n: int) = for h in List.ofSeq handlers do h.DynamicInvoke(box n) |> ignore
    member _.HandlerCount = handlers.Count
    override _.TryGetMember(binder, result) =
        if binder.Name = "Changed" then result <- box (BoundEvent handlers); true else false
    override _.TrySetMember(binder, value) = binder.Name = "Changed" && (value :? BoundEvent)

and BoundEvent(handlers: ResizeArray<Delegate>) =
    inherit DynamicObject()
    override this.TryBinaryOperation(binder, arg, result) =
        match binder.Operation, arg with
        | Linq.Expressions.ExpressionType.AddAssign, (:? Delegate as d) -> handlers.Add d; result <- box this; true
        | Linq.Expressions.ExpressionType.SubtractAssign, (:? Delegate as d) -> handlers.Remove d |> ignore; result <- box this; true
        | _ -> raise (InvalidOperationException "Attempting to pass an event handler of an unsupported type.")

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
    /// A module-level target: a block over it captures nothing.
    let plainWidget: obj = Widget()

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

// Structural types for the equality/comparison binder: no CLR operators, F# semantics.
type Point = { X: int; Y: int }
[<Struct>]
type SPoint = { SX: int; SY: int }
type Shape =
    | Circle of int
    | Rect of int * int
type Money(amount: int) =
    member _.Amount = amount
    /// A class with its own CLR operator: C# binds it, structural rules stay out.
    static member op_Equality(a: Money, b: Money) = a.Amount = b.Amount
    static member op_Inequality(a: Money, b: Money) = a.Amount <> b.Amount
    override _.Equals(o) = match o with :? Money as m -> m.Amount = amount | _ -> false
    override _.GetHashCode() = amount
/// Equal by Equals, not comparable.
type Opaque(tag: string) =
    member _.Tag = tag
    override _.Equals(o) = match o with :? Opaque as x -> x.Tag = tag | _ -> false
    override _.GetHashCode() = tag.GetHashCode()
/// A DynamicObject that answers `==` itself: dynamic targets keep their own say.
type EqualsAnything() =
    inherit DynamicObject()
    override _.TryBinaryOperation(binder, _arg, result) =
        match binder.Operation with
        | System.Linq.Expressions.ExpressionType.Equal -> result <- box true; true
        | _ -> result <- null; false

// Constructors chosen by the arguments' runtime types (Dlr.new').
type Handler(kind: string, detail: string) =
    new(c: Point) = Handler("point", string c.X + "," + string c.Y)
    new(s: Shape) = Handler("shape", sprintf "%A" s)
    new(o: obj) = Handler("obj", string o)
    new() = Handler("none", "")
    new(name: string, count: int) = Handler("named", name + ":" + string count)
    member _.Kind = kind
    member _.Detail = detail

// Inline members: the compiled method is real, and operator constraints resolve at run time
// through F#'s dynamic operator implementations, but a member constraint cannot.
type Vec(x: float) =
    member _.X = x
    static member (+) (a: Vec, b: Vec) = Vec(a.X + b.X)
type Inlines() =
    member inline _.Twice(v: ^T) : ^T = v + v
    member inline _.NameOf(v: ^T when ^T: (member Name: string)) : string = (^T: (member Name: string) v)

/// Past sixteen parameters no adapter class exists, so a function converted to this one is applied
/// through `FunctionShapes.applyCall` — where a tupled domain's tuple nests.
type Wide17 = delegate of int * int * int * int * int * int * int * int * int * int * int * int * int * int * int * int * int -> int

/// F# compiles a delegate's `Invoke` at the type's own accessibility, so this one's is not public.
type internal InternalHandler = delegate of int -> int
type internal InternalAdd = delegate of int * int -> int
type internal InternalSix = delegate of int * int * int * int * int * int -> int
type internal InternalSixAction = delegate of int * int * int * int * int * int -> unit
type internal InternalApply = delegate of (int -> int) * int -> int
type internal InternalThunk = delegate of unit -> string
type internal InternalWide17 = delegate of int * int * int * int * int * int * int * int * int * int * int * int * int * int * int * int * int -> int

// Delegate and F# function parameters, for the conversions C# does not do (F# lambda -> Func,
// Func -> F# function) at a dynamic call.
type Callbacks() =
    member val Log = ResizeArray<string>() with get
    member this.Each(items: int list, action: Action<int>) = for i in items do action.Invoke i
    member _.Map(x: int, f: Func<int, int>) = f.Invoke x
    member _.Fold(a: int, b: int, f: Func<int, int, int>) = f.Invoke(a, b)
    member _.Apply(x: int, f: int -> int) = f x
    member _.Apply2(a: int, b: int, f: int -> int -> int) = f a b
    member _.ApplyTupled(a: int, b: int, f: int * int -> int) = f (a, b)
    member _.Run(f: unit -> string) = f ()
    member _.Pick(x: int, f: Func<int, int>) = "func:" + string (f.Invoke x)
    member _.Pick(_: int, s: string) = "string:" + s
    member _.Six(f: Func<int, int, int, int, int, int, int>) = f.Invoke(1, 2, 3, 4, 5, 6)
    member _.Six'(f: int -> int -> int -> int -> int -> int -> int) = f 1 2 3 4 5 6
    member _.SixPartial(f: int -> int -> int -> int -> int -> int -> int) = let g = f 1 2 3 in g 4 5 6 * 1000 + g 0 0 0
    member _.SixTupled'(f: int * int * int * int * int * int -> int) = f (1, 2, 3, 4, 5, 6)
    member _.EightTupled'(f: int * int * int * int * int * int * int * int -> int) = f (1, 2, 3, 4, 5, 6, 7, 8)
    member _.SixUnit'(f: int -> int -> int -> int -> int -> int -> unit) = f 1 2 3 4 5 6
    member val internal Adder: InternalAdd = InternalAdd(fun a b -> a * 100 + b) with get
    member internal _.WideInternal(f: InternalWide17) = f.Invoke(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)
    member _.Wide17Tupled(f: int * int * int * int * int * int * int * int * int * int * int * int * int * int * int * int * int -> int) = f (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)
    member _.Wide17Curried(f: int -> int -> int -> int -> int -> int -> int -> int -> int -> int -> int -> int -> int -> int -> int -> int -> int -> int) = f 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17
    member _.Keep(f: Func<int, int>) = f
    member _.Wide(f: Wide17) = f.Invoke(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)
    member internal _.KeepInternal(f: InternalHandler) = f
    member _.Raw(d: Delegate) = d.GetType().Name                 // WinForms' Control.Invoke(Delegate) shape
    member _.Marshal(d: Delegate) = d.DynamicInvoke() |> string   // and how it uses it

// A static overload set (Static<T>.Overloads): overloads picked by the runtime type of an obj argument.
type Renderer private () =
    static member Draw(p: Point) = "point " + string p.X
    static member Draw(s: Shape) = "shape " + (match s with Circle r -> string r | Rect _ -> "rect")
    static member Draw(o: obj) = "obj " + string o
    static member val Scale = 1.0 with get, set
    static member Parse<'T>(s: string) : 'T = System.Convert.ChangeType(s, typeof<'T>) :?> 'T
    static member private Secret() = "secret"

// The F#-aware argument rules on every kind of target (#49): static methods, constructors,
// delegate members and delegate values.
type Statics private () =
    static member BumpF(count: int, ?step: int) = count + defaultArg step 1
    static member SumAll([<ParamArray>] xs: int[]) = Array.sum xs
    static member Run(f: Func<int, int>) = f.Invoke 21
    static member Apply(x: int, f: int -> int) = f x
type Ctor(count: int, ?step: int) =
    member _.Value = count + defaultArg step 1
type CtorF(f: Func<int, int>) =
    member _.Value = f.Invoke 21
type CtorFn(f: int -> int) =
    member _.Value = f 21
/// A disposable that records its disposal, for `use` inside a block.
type Disposal(log: Collections.Generic.List<string>, name: string) =
    member _.Name = name
    interface IDisposable with
        member _.Dispose() = log.Add("disposed " + name)

/// Slots typed as a delegate or an F# function, assigned the other kind (#153).
type Slots() =
    member val Handler: Func<int, int, int> = Func<int, int, int>(fun a b -> a + b) with get, set
    member val Notify: Action<string> = Action<string>(ignore) with get, set
    member val internal Adder: InternalAdd = InternalAdd(fun a b -> a + b) with get, set
    member val Fn: int -> int -> int = (fun a b -> a + b) with get, set
    member val Wide: Func<int, int, int, int, int, int, int> = null with get, set
    member val Conv: Converter<int, int> = null with get, set
    [<DefaultValue>] val mutable Field: Func<int, int>
    member val Handlers = Collections.Generic.Dictionary<string, Func<int, int>>() with get
    member val Functions = Collections.Generic.Dictionary<string, int -> int>() with get
    member val Array: Func<int, int>[] = Array.zeroCreate 2 with get
    member val Thunk: Func<int> = null with get, set
    member _.Call(f: Func<int>) = f.Invoke() * 2

type DelegateMembers() =
    member val Run: Func<Func<int, int>, int> = Func<Func<int, int>, int>(fun f -> f.Invoke 21) with get
    member val Apply: (int -> int) -> int = (fun f -> f 21) with get
