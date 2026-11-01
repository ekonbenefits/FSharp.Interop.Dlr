module Tests.LiteralLib.Literal

open FSharp.Interop.Dlr

[<ReflectedDefinition>]
module Reflected =
    /// FSharp.Core records it with the module's reflected definitions and cannot bind it.
    [<Literal>]
    let Hundred = 100

    let addHundred (target: obj) (n: int) : int = dlr { return target?Add(n, Hundred) }
