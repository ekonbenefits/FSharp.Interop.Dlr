/// Blocks in a library of their own, for callers in another assembly (#180).
module Tests.FSharpLib.Blocks

open FSharp.Interop.Dlr

/// A small function holding a block: the kind the Release optimizer may inline into its callers.
[<ReflectedDefinition>]
let count (o: obj) : int = dlr { return o?Count }
