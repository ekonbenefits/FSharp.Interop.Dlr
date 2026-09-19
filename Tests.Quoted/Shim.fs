/// Every `dlr { }` in the linked Tests sources becomes a `dlrq { }`: an auto-opened module in
/// the library's namespace, so each file's `open FSharp.Interop.Dlr` brings this `dlr` in after
/// the library's. Shared semantics are then tested once, over both builders; what differs is
/// under `#if DLRQ` in the shared files, and what only dlrq needs is in Tests/Quoted.fs.
namespace FSharp.Interop.Dlr

[<AutoOpen>]
module QuotedShim =
    let dlr = Builder.dlrq
