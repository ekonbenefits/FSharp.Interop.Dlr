namespace FSharp.Interop.Dlr

open System
open System.Collections.Concurrent

/// The compiled-delegate cache behind `dlr { }`, keyed by the Delay closure's compiler-generated
/// type, which is unique per block. Exposed for diagnostics and tests.
module DlrCache =

    let private cache = ConcurrentDictionary<Type, Translate.Compiled>()

    /// Number of compiled `dlr { }` sites.
    let count () = cache.Count

    /// Drops every compiled site; the next call at each site recompiles.
    let clear () = cache.Clear()

    let internal getOrCompile (builderType: Type) (closureType: Type) (file: string) (line: int) (resultType: Type) =
        match cache.TryGetValue closureType with
        | true, compiled -> compiled
        | _ ->
            cache.GetOrAdd(closureType, fun t ->
                let found = Discover.findBody builderType t file line
                Translate.translate builderType found.Context found.MemberBody t resultType found.Body)
