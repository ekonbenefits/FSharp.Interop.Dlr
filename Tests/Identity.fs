/// The library's assembly identity, a contract once released: strong-named consumers bind to its
/// public key token, so a replaced key or a delay-signed build would break them while still
/// building here. Each test leg checks the build it loads (net10.0, or netstandard2.0 on net48).
module Tests.Identity

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr

let private library = typeof<DlrRun>.Assembly

[<Fact>]
let ``the library's public key token never changes`` () =
    library.GetName().GetPublicKeyToken() |> Array.map (sprintf "%02x") |> String.concat ""
    |> should equal "b83ba7a2fe4e5456"

[<Fact>]
let ``the library is fully strong-name signed, not delay-signed`` () =
    // Browser-wasm loads assemblies from memory: no file to read the header from.
    if String.IsNullOrEmpty library.Location then raise (AnyUnit.IgnoreException "no assembly file on this runtime")
    use reader = new Reflection.PortableExecutable.PEReader(IO.File.OpenRead library.Location)
    let header = reader.PEHeaders.CorHeader
    header.Flags.HasFlag Reflection.PortableExecutable.CorFlags.StrongNameSigned |> should equal true
    header.StrongNameSignatureDirectory.Size |> should be (greaterThan 0)
