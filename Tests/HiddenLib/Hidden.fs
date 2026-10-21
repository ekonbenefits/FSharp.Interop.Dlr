module Tests.HiddenLib.Hidden

open FSharp.Interop.Dlr

/// Not in the signature: Release inlines it at its one call and erases it.
let wrap (x: int) = x + 1

[<ReflectedDefinition>]
let reflectedCaller (target: obj) (n: int) : int =
    let s = wrap n
    dlr { return target?Add(s, 0) }

/// A block in a local function: its closure is named after `inner`, not this member.
[<ReflectedDefinition>]
let nestedLocal (target: obj) (n: int) : int =
    let inner (k: int) : int = dlr { return target?Add(k, 0) }
    inner n + inner 0
