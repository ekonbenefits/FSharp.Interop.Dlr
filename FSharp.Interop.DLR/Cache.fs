namespace FSharp.Interop.DLR

open System
open System.Collections.Concurrent
open FSharp.Quotations

/// The compiled-delegate cache behind `dlr { }`, keyed by the source file and line of each block.
/// Exposed for diagnostics and tests.
module DlrCache =

    let private cache = ConcurrentDictionary<struct (string * int), Translate.Compiled>()

    /// Number of compiled `dlr { }` sites.
    let count () = cache.Count

    /// Drops every compiled site; the next call at each site recompiles.
    let clear () = cache.Clear()

    let internal getOrCompile (builderType: Type) (file: string) (line: int) (resultType: Type) (quotation: Expr) =
        let compile () = Translate.translate builderType resultType quotation
        if line = 0 then
            // No caller info (e.g. Run invoked via reflection): nothing safe to key on.
            compile ()
        else
            let compiled = cache.GetOrAdd(struct (file, line), fun _ -> compile ())
            // Two blocks on one line share a key; a shape mismatch is the cheap way to notice.
            let slotCount = (Translate.extractSlots quotation).Length
            if compiled.ResultType <> resultType || compiled.SlotCount <> slotCount then
                raise (InvalidOperationException(
                        sprintf "Two different dlr { } blocks share %s:%d. The cache is keyed by file and line, so put each dlr { } on its own line." file line))
            compiled
