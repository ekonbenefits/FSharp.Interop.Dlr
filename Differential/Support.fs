/// What the generated cases call: not quoted, so the counter can be mutable.
module Differential.Support

open System.Runtime.CompilerServices

/// Numbers each call, so a definition run again shows.
type Ticks() =
    static let mutable n = 0
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Next() = n <- n + 1; n
    static member Reset() = n <- 0

/// A struct value: a captured one is read back as a copy.
[<Struct>]
type S(v: int) =
    member _.V = v

/// Applies `h`, inlined with it wherever it is called (`InlineIfLambda`).
let inline applyInline ([<InlineIfLambda>] h: unit -> 'T) : 'T = h ()

/// The block's target: it hands back what it was given.
type Echo() =
    member _.Echo(a: int) = string a
    member _.Echo(a: int, b: int) = sprintf "%d|%d" a b
    member _.Echo(a: int, b: int, c: int) = sprintf "%d|%d|%d" a b c
