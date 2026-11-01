/// A block in another file of the same assembly, with no literal near it.
module Tests.LiteralLib.Elsewhere

open FSharp.Interop.Dlr

[<ReflectedDefinition>]
let independent (target: obj) (n: int) : int =
    let m = n * 2
    dlr { return target?Add(m, 0) }
