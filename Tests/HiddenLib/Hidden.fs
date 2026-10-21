module Tests.HiddenLib.Hidden

open FSharp.Interop.Dlr

/// Not in the signature: Release inlines it at its one call and erases it.
let wrap (x: int) = x + 1

[<ReflectedDefinition>]
let reflectedCaller (target: obj) (n: int) : int =
    let s = wrap n
    dlr { return target?Add(s, 0) }
