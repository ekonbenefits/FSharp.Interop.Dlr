namespace FSharp.Interop.Dlr

open System
open System.Runtime.CompilerServices

// Every marker below is [<MethodImpl(MethodImplOptions.NoInlining)>]: the F# optimizer inlines
// small functions across assemblies, and after inlining `ignore (target, name, value); outside …`
// the arguments are dead, so a Release build stops capturing them in the block's closure - and
// the translator then has nothing to read. The attribute keeps the call, and its arguments, intact.
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
        with [<MethodImpl(MethodImplOptions.NoInlining)>] get (i: obj) : 'T = ignore i; outside "Dlr.idx"
        and [<MethodImpl(MethodImplOptions.NoInlining)>] set (i: obj) (v: 'T) = ignore (i, v); outside "Dlr.idx"
    member _.Item
        with [<MethodImpl(MethodImplOptions.NoInlining)>] get (i: obj, j: obj) : 'T = ignore (i, j); outside "Dlr.idx"
        and [<MethodImpl(MethodImplOptions.NoInlining)>] set (i: obj, j: obj) (v: 'T) = ignore (i, j, v); outside "Dlr.idx"
    member _.Item
        with [<MethodImpl(MethodImplOptions.NoInlining)>] get (i: obj, j: obj, k: obj) : 'T = ignore (i, j, k); outside "Dlr.idx"
        and [<MethodImpl(MethodImplOptions.NoInlining)>] set (i: obj, j: obj, k: obj) (v: 'T) = ignore (i, j, k, v); outside "Dlr.idx"
    member _.Item
        with [<MethodImpl(MethodImplOptions.NoInlining)>] get (i: obj, j: obj, k: obj, l: obj) : 'T = ignore (i, j, k, l); outside "Dlr.idx"
        and [<MethodImpl(MethodImplOptions.NoInlining)>] set (i: obj, j: obj, k: obj, l: obj) (v: 'T) = ignore (i, j, k, l, v); outside "Dlr.idx"

[<AutoOpen>]
module Operators =

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ? ) (target: obj) (name: string) : 'TResult = ignore (target, name); outside "?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?<- ) (target: obj) (name: string) (value: 'TValue) : unit = ignore (target, name, value); outside "?<-"

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?%? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?%?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?*? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?*?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?+? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?+?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?-? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?-?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?/? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?/?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?&&&? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?&&&?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?|||? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?|||?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?^^^? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?^^^?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?<<<? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?<<<?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?>>>? ) (left: obj) (right: obj) : 'TResult = ignore (left, right); outside "?>>>?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?<=? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?<=?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?<>? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?<>?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?<? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?<?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?=? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?=?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?>? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?>?"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let ( ?>=? ) (left: obj) (right: obj) : bool = ignore (left, right); outside "?>=?"

[<Sealed; AbstractClass>]
type Dlr =
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member named (record: 'T) : Named<'T> = ignore record; outside "Dlr.named"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member idx (target: obj) : Indexed<'T> = ignore target; outside "Dlr.idx"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member typeArgs<'A> () : TypeArgs = outside "Dlr.typeArgs"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member typeArgs<'A, 'B> () : TypeArgs = outside "Dlr.typeArgs"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member typeArgs<'A, 'B, 'C> () : TypeArgs = outside "Dlr.typeArgs"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member typeArgs<'A, 'B, 'C, 'D> () : TypeArgs = outside "Dlr.typeArgs"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member cast<'T> (value: obj) : 'T = ignore value; outside "Dlr.cast"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member get (name: string) (target: obj) : 'T = ignore (name, target); outside "Dlr.get"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member force (target: obj) : 'T = ignore target; outside "Dlr.force"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member set (name: string) (value: 'TValue) (target: obj) : unit = ignore (name, value, target); outside "Dlr.set"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member invoke (name: string) (args: 'TArgs) (target: obj) : 'T = ignore (name, args, target); outside "Dlr.invoke"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member implicit (value: obj) : 'T = ignore value; outside "Dlr.implicit"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member neg (value: obj) : 'TResult = ignore value; outside "Dlr.neg"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member not (value: obj) : 'TResult = ignore value; outside "Dlr.not"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member complement (value: obj) : 'TResult = ignore value; outside "Dlr.complement"
