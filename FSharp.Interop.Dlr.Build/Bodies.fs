namespace FSharp.Interop.Dlr.Build

open System.Collections.Generic
open FSharp.Compiler.CodeAnalysis

/// The capture map's `body` section: each block's member body, serialized, so a block needs no
/// [<ReflectedDefinition>] (the block-bodies follow-up, stacked on #216). A stub until it lands.
module Bodies =

    /// Per block key (file, line), the lines of its `body` section.
    let run (_results: FSharpCheckProjectResults) : IDictionary<string * int, string list> = dict []
