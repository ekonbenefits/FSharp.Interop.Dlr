/// A block in another file of the same assembly, calling nothing hidden.
module Tests.HiddenLib.Elsewhere

open FSharp.Interop.Dlr

[<ReflectedDefinition>]
let independent (target: obj) (n: int) : int =
    let m = n * 2
    dlr { return target?Add(m, 0) }
