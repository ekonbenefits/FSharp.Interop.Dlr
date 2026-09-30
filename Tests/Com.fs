/// COM through `IDispatch`: the C# binder's other half (`Microsoft.CSharp.RuntimeBinder.ComInterop`),
/// on the COM servers every Windows box has — `Scripting.FileSystemObject` and `WScript.Shell`. A
/// COM object reaches a block as a `System.__ComObject` with no metadata to bind against, so every
/// form below is late-bound by name through `IDispatch`, as C#'s `dynamic` does it. Skipped where
/// the ProgID is not registered (anything but Windows).
[<ReflectedDefinition>]
module Tests.Com

open System
open System.IO
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

// --- the fixture ----------------------------------------------------------------------------

let private create (progId: string) : obj =
    let t = try Type.GetTypeFromProgID progId with _ -> null
    if isNull t then raise (AnyUnit.IgnoreException (progId + " is not registered (COM is Windows-only)"))
    Activator.CreateInstance t

/// A fresh folder holding `a.txt` and `b.txt`, removed afterwards.
let private withFolder (test: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), "dlr-com-" + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    File.WriteAllText(Path.Combine(dir, "a.txt"), "a")
    File.WriteAllText(Path.Combine(dir, "b.txt"), "bb")
    try
        test dir
    finally
        for f in Directory.GetFiles dir do
            File.SetAttributes(f, FileAttributes.Normal)
        Directory.Delete(dir, true)

// --- the tests ------------------------------------------------------------------------------

[<Fact>]
let ``methods with typed arguments`` () =
    let fso = create "Scripting.FileSystemObject"
    withFolder (fun dir ->
        let exists: bool = dlr { return fso?FolderExists(dir) }
        let missing: bool = dlr { return fso?FolderExists(Path.Combine(dir, "nope")) }
        let name: string = dlr { return fso?GetFileName(Path.Combine(dir, "a.txt")) }
        exists |> should equal true
        missing |> should equal false
        name |> should equal "a.txt")

[<Fact>]
let ``chained property reads`` () =
    let fso = create "Scripting.FileSystemObject"
    withFolder (fun dir ->
        let count: int = dlr { return fso?GetFolder(dir)?Files?Count }
        let size: int = dlr { return fso?GetFile(Path.Combine(dir, "b.txt"))?Size }
        count |> should equal 2
        size |> should equal 2)

[<Fact>]
let ``for over a COM collection`` () =
    let fso = create "Scripting.FileSystemObject"
    withFolder (fun dir ->
        let names = ResizeArray<string>()
        dlr {
            for f in Seq.cast<obj> (fso?GetFolder(dir)?Files : Collections.IEnumerable) do     // IEnumVARIANT
                names.Add(f?Name)
        }
        List.sort (List.ofSeq names) |> should equal [ "a.txt"; "b.txt" ])

[<Fact>]
let ``a property set`` () =
    let fso = create "Scripting.FileSystemObject"
    withFolder (fun dir ->
        let file: obj = dlr { return fso?GetFile(Path.Combine(dir, "a.txt")) }
        dlr { file?Attributes <- 1 }                                             // ReadOnly
        let attributes: int = dlr { return file?Attributes }
        attributes &&& 1 |> should equal 1
        File.GetAttributes(Path.Combine(dir, "a.txt")).HasFlag FileAttributes.ReadOnly |> should equal true)

[<Fact>]
let ``WScript.Shell expands environment strings`` () =
    let shell = create "WScript.Shell"
    let expanded: string = dlr { return shell?ExpandEnvironmentStrings("%TEMP%") }
    expanded |> should equal (Environment.ExpandEnvironmentVariables "%TEMP%")

[<Fact>]
let ``a computed name`` () =
    let fso = create "Scripting.FileSystemObject"
    let call (name: string) (path: string) : string = dlr { return ((?) fso name) (path) }
    call "GetExtensionName" @"C:\x\y.txt" |> should equal "txt"
    call "GetBaseName" @"C:\x\y.txt" |> should equal "y"

[<Fact>]
let ``a miss is a RuntimeBinderException`` () =
    let fso = create "Scripting.FileSystemObject"
    (fun () -> (dlr { return fso?NoSuchMember(1) } : obj) |> ignore) |> should throw typeof<RuntimeBinderException>
    (fun () -> (dlr { return fso?NoSuchProperty } : obj) |> ignore) |> should throw typeof<RuntimeBinderException>

[<Fact>]
let ``one site serves a COM object and a CLR object`` () =
    let fso = create "Scripting.FileSystemObject"
    withFolder (fun dir ->
        let file: obj = dlr { return fso?GetFile(Path.Combine(dir, "a.txt")) }
        let folder: obj = dlr { return fso?GetFolder(dir) }
        let targets = [ file; box (Widget()); folder ]
        let names = ResizeArray<string>()
        dlr {
            for t in targets do
                names.Add(t?Name)
        }
        List.ofSeq names |> should equal [ "a.txt"; "widget"; Path.GetFileName dir ])
