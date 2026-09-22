/// Every generated adapter in `Adapters.fs` (from `generate-adapters.fsx`), by table: the type
/// exists for each arity, its `Invoke` has the shape the library looks up by name, and it wires
/// through to the function or delegate it wraps. The library reaches these by reflection on name
/// and arity, so a generator slip would otherwise surface only when that exact shape is converted.
module Tests.Adapters

open System
open System.Reflection
open FSharp.Reflection
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

/// Static bodies for the delegates, one per arity: a Func summing, an Action recording.
type Bodies private () =
    static member val Hits: ResizeArray<int> = ResizeArray() with get, set
    static member Sum0() = 0
    static member Sum1(a: int) = a
    static member Sum2(a: int, b: int) = a + b
    static member Sum3(a: int, b: int, c: int) = a + b + c
    static member Sum4(a: int, b: int, c: int, d: int) = a + b + c + d
    static member Sum5(a: int, b: int, c: int, d: int, e: int) = a + b + c + d + e
    static member Record0() = ()
    static member Record1(a: int) = Bodies.Hits.Add a
    static member Record2(a: int, b: int) = Bodies.Hits.AddRange [ a; b ]
    static member Record3(a: int, b: int, c: int) = Bodies.Hits.AddRange [ a; b; c ]
    static member Record4(a: int, b: int, c: int, d: int) = Bodies.Hits.AddRange [ a; b; c; d ]
    static member Record5(a: int, b: int, c: int, d: int, e: int) = Bodies.Hits.AddRange [ a; b; c; d; e ]

let private ints (n: int) = Array.create n typeof<int>
let private nested (holder: Type) (name: string) (arity: int) =
    holder.GetNestedType(name + (if arity > 0 then "`" + string arity else ""))
let private invoke (o: obj) (args: obj[]) = o.GetType().GetMethod("Invoke").Invoke(o, args)
let private triangle n = n * (n + 1) / 2

/// `int -> … -> int -> R`, `n` deep.
let rec private curriedType (n: int) (result: Type) =
    if n = 0 then result else FSharpType.MakeFunctionType(typeof<int>, curriedType (n - 1) result)
/// A curried F# function of `n` ints, built by reflection, ending in `finish` over them in order.
let rec private curried (collected: int list) (remaining: int) (result: Type) (finish: int list -> obj) : obj =
    FSharpValue.MakeFunction(curriedType remaining result, fun a ->
        let xs = unbox<int> a :: collected
        if remaining = 1 then finish (List.rev xs) else curried xs (remaining - 1) result finish)

// --- FunctionAdapters: an F# function presented as a method with the delegate's signature ------

let private functionAdapters = typeof<FunctionAdapters.Curried0Unit>.DeclaringType

[<Fact>]
let ``FunctionAdapters: curried and tupled, result and unit, 0 to 16 parameters`` () =
    for n in 0 .. 16 do
        for tupled in (if n >= 2 then [ false; true ] else [ false ]) do
            for unitResult in [ false; true ] do
                let name = (if tupled then "Tupled" else "Curried") + string n + (if unitResult then "Unit" else "")
                let arity = n + (if unitResult then 0 else 1)
                let def = nested functionAdapters name arity
                if isNull def then failwithf "%s`%d is missing" name arity
                let resultType = if unitResult then typeof<unit> else typeof<int>
                let finish (xs: int list) = if unitResult then box () else box (List.sum xs)
                let fn: obj =
                    if n = 0 then FSharpValue.MakeFunction(FSharpType.MakeFunctionType(typeof<unit>, resultType), fun _ -> finish [])
                    elif tupled then
                        let tupleType = FSharpType.MakeTupleType(ints n)
                        FSharpValue.MakeFunction(FSharpType.MakeFunctionType(tupleType, resultType), fun t -> finish [ for v in FSharpValue.GetTupleFields t -> unbox<int> v ])
                    else curried [] n resultType finish
                let closed = if arity = 0 then def else def.MakeGenericType(Array.append (ints n) (if unitResult then [||] else [| typeof<int> |]))
                let adapter = Activator.CreateInstance(closed, [| fn |])
                let m = closed.GetMethod("Invoke")
                m.GetParameters().Length |> should equal n
                (m.ReturnType = (if unitResult then typeof<Void> else typeof<int>)) |> should equal true
                let r = invoke adapter [| for i in 1 .. n -> box i |]
                if not unitResult then (r :?> int) |> should equal (triangle n)

// --- DelegateFunctions: a delegate presented as an F# function ------------------------------

let private delegateFunctions = typeof<DelegateFunctions.Action0<unit>>.DeclaringType

[<Fact>]
let ``DelegateFunctions: Func and Action, curried and tupled, 0 to 5 parameters`` () =
    for n in 0 .. 5 do
        for tupled in (if n >= 2 then [ false; true ] else [ false ]) do
            for isAction in [ false; true ] do
                let name = (if tupled then "Tupled" else "") + (if isAction then "Action" else "Func") + string n
                let def = nested delegateFunctions name (n + 1)       // 'T1 … 'Tn and 'R
                if isNull def then failwithf "%s`%d is missing" name (n + 1)
                let closed = def.MakeGenericType(Array.append (ints n) [| typeof<int> |])
                let delegateType =
                    if isAction then (if n = 0 then typeof<Action> else Type.GetType("System.Action`" + string n).MakeGenericType(ints n))
                    else Type.GetType("System.Func`" + string (n + 1)).MakeGenericType(Array.append (ints n) [| typeof<int> |])
                let body = typeof<Bodies>.GetMethod((if isAction then "Record" else "Sum") + string n)
                let d = Delegate.CreateDelegate(delegateType, body)
                let f = Activator.CreateInstance(closed, [| box d |])
                Bodies.Hits.Clear()
                let result =
                    if n = 0 then invoke f [| box () |]
                    elif tupled then invoke f [| FSharpValue.MakeTuple([| for i in 1 .. n -> box i |], FSharpType.MakeTupleType(ints n)) |]
                    else
                        // Curried: apply one argument at a time through FSharpFunc.Invoke.
                        let mutable cur = f
                        for i in 1 .. n do cur <- cur.GetType().GetMethod("Invoke", [| typeof<int> |]).Invoke(cur, [| box i |])
                        cur
                if isAction then Seq.sum Bodies.Hits |> should equal (triangle n)
                else (result :?> int) |> should equal (triangle n)
