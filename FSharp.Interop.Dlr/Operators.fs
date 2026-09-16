namespace FSharp.Interop.Dlr

open System

module internal Outside =
    let outside (name: string) : 'T =
        raise (InvalidOperationException(sprintf "'%s' is only meaningful inside dlr { }; it is inspected as a quotation, never executed." name))

open Outside

[<Sealed>]
type Named<'T> private () =
    class end

[<Sealed>]
type TypeArgs private () =
    class end

[<Sealed>]
type Indexed<'T> private () =
    member _.Item
        with get (i: obj) : 'T = ignore i; outside "Dlr.idx"
        and set (i: obj) (v: 'T) = ignore (i, v); outside "Dlr.idx"
    member _.Item
        with get (i: obj, j: obj) : 'T = ignore (i, j); outside "Dlr.idx"
        and set (i: obj, j: obj) (v: 'T) = ignore (i, j, v); outside "Dlr.idx"
    member _.Item
        with get (i: obj, j: obj, k: obj) : 'T = ignore (i, j, k); outside "Dlr.idx"
        and set (i: obj, j: obj, k: obj) (v: 'T) = ignore (i, j, k, v); outside "Dlr.idx"
    member _.Item
        with get (i: obj, j: obj, k: obj, l: obj) : 'T = ignore (i, j, k, l); outside "Dlr.idx"
        and set (i: obj, j: obj, k: obj, l: obj) (v: 'T) = ignore (i, j, k, l, v); outside "Dlr.idx"

[<AutoOpen>]
module Operators =

    let ( ? ) (target: obj) (name: string) : 'TResult = ignore (target, name); outside "?"
    let ( ?<- ) (target: obj) (name: string) (value: 'TValue) : unit = ignore (target, name, value); outside "?<-"
    let ( !? ) (target: obj) : 'TResult = ignore target; outside "!?"

    let ( ?%? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?%?"
    let ( ?*? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?*?"
    let ( ?+? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?+?"
    let ( ?-? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?-?"
    let ( ?/? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?/?"
    let ( ?&&&? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?&&&?"
    let ( ?|||? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?|||?"
    let ( ?^^^? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?^^^?"
    let ( ?<<<? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?<<<?"
    let ( ?>>>? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?>>>?"
    let ( ?<=? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?<=?"
    let ( ?<>? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?<>?"
    let ( ?<? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?<?"
    let ( ?=? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?=?"
    let ( ?>? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?>?"
    let ( ?>=? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?>=?"

[<Sealed; AbstractClass>]
type Dlr =
    static member named (record: 'T) : Named<'T> = ignore record; outside "Dlr.named"
    static member idx (target: obj) : Indexed<'T> = ignore target; outside "Dlr.idx"
    static member typeArgs<'A> () : TypeArgs = outside "Dlr.typeArgs"
    static member typeArgs<'A, 'B> () : TypeArgs = outside "Dlr.typeArgs"
    static member typeArgs<'A, 'B, 'C> () : TypeArgs = outside "Dlr.typeArgs"
    static member typeArgs<'A, 'B, 'C, 'D> () : TypeArgs = outside "Dlr.typeArgs"
