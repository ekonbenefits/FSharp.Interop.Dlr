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
type OutArg private () =
    class end

[<Sealed>]
type RefArg<'T> private () =
    class end

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
    static member namedOf (args: (string * obj) list) : Named<(string * obj) list> = ignore args; outside "Dlr.namedOf"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member argsOf (args: obj list) : Named<obj list> = ignore args; outside "Dlr.argsOf"
    static member out with [<MethodImpl(MethodImplOptions.NoInlining)>] get () : OutArg = outside "Dlr.out"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member ref (variable: 'T) : RefArg<'T> = ignore variable; outside "Dlr.ref"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member item (indexes: 'TIndexes) (target: obj) : 'T = ignore (indexes, target); outside "Dlr.item"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member setItem (indexes: 'TIndexes) (value: 'TValue) (target: obj) : unit = ignore (indexes, value, target); outside "Dlr.setItem"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member typeArgs<'A> () : TypeArgs = outside "Dlr.typeArgs"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member typeArgs<'A, 'B> () : TypeArgs = outside "Dlr.typeArgs"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member typeArgs<'A, 'B, 'C> () : TypeArgs = outside "Dlr.typeArgs"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member typeArgs<'A, 'B, 'C, 'D> () : TypeArgs = outside "Dlr.typeArgs"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member typeArgsOf (types: System.Type list) : TypeArgs = ignore types; outside "Dlr.typeArgsOf"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member new'<'T> () : 'T = outside "Dlr.new'"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member new'<'T> (a: obj) : 'T = ignore (a); outside "Dlr.new'"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member new'<'T> (a: obj, b: obj) : 'T = ignore (a, b); outside "Dlr.new'"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member new'<'T> (a: obj, b: obj, c: obj) : 'T = ignore (a, b, c); outside "Dlr.new'"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member new'<'T> (a: obj, b: obj, c: obj, d: obj) : 'T = ignore (a, b, c, d); outside "Dlr.new'"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member new'<'T> (a: obj, b: obj, c: obj, d: obj, e: obj) : 'T = ignore (a, b, c, d, e); outside "Dlr.new'"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member new'<'T> (a: obj, b: obj, c: obj, d: obj, e: obj, f: obj) : 'T = ignore (a, b, c, d, e, f); outside "Dlr.new'"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member new'<'T> (a: obj, b: obj, c: obj, d: obj, e: obj, f: obj, g: obj) : 'T = ignore (a, b, c, d, e, f, g); outside "Dlr.new'"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member new'<'T> (a: obj, b: obj, c: obj, d: obj, e: obj, f: obj, g: obj, h: obj) : 'T = ignore (a, b, c, d, e, f, g, h); outside "Dlr.new'"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member cast<'T> (value: obj) : 'T = ignore value; outside "Dlr.cast"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member get (name: string) (target: obj) : 'T = ignore (name, target); outside "Dlr.get"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member call (target: obj) : 'T = ignore target; outside "Dlr.call"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member apply (args: 'TArgs) (target: obj) : 'T = ignore (args, target); outside "Dlr.apply"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member addAssign (name: string) (value: 'TValue) (target: obj) : unit = ignore (name, value, target); outside "Dlr.addAssign"
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member subtractAssign (name: string) (value: 'TValue) (target: obj) : unit = ignore (name, value, target); outside "Dlr.subtractAssign"
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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Dlr =
    [<Sealed; AbstractClass>]
    type Static<'T> =
        static member Overloads
            with [<MethodImpl(MethodImplOptions.NoInlining)>] get () : obj = outside "Dlr.Static<T>.Overloads"
