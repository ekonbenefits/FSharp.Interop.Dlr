/// COM through `IDispatch`: the C# binder's other half (`Microsoft.CSharp.RuntimeBinder.ComInterop`),
/// on the COM servers every Windows box has — `Scripting.FileSystemObject` and `WScript.Shell`. A
/// COM object reaches a block as a `System.__ComObject` with no metadata to bind against, so every
/// form below is late-bound by name through `IDispatch`, as C#'s `dynamic` does it: optional
/// arguments omitted or passed by name (the binder fills `Type.Missing`), and events through C#'s
/// COM event sink, on ADO's in-memory `ADODB.Recordset`, whose `MoveComplete` fires on the calling
/// thread. A Dlr.ref reaches a COM method as a by-reference VARIANT (ADODB.Stream, Scripting.Dictionary),
/// and a COM [out] is written back through Dlr.out (ADO's Connection.Execute against SQL Server
/// LocalDB, which the Windows CI runner has). Skipped where the ProgID is not registered (anything
/// but Windows) or LocalDB does not open.
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

/// An in-memory recordset (no connection) of one integer field `A` holding 1, 2 and 3.
let private recordset () : obj =
    let rs = create "ADODB.Recordset"
    dlr { rs?Fields?Append("A", 3) }           // adInteger; DefinedSize, Attrib and FieldValue omitted
    dlr { rs?Open() }                          // every argument omitted
    for i in 1 .. 3 do
        dlr { rs?AddNew("A", i) }
    rs

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

[<Fact>]
let ``optional COM arguments omitted`` () =
    let fso = create "Scripting.FileSystemObject"
    withFolder (fun dir ->
        let path = Path.Combine(dir, "c.txt")
        let writer: obj = dlr { return fso?CreateTextFile(path) }          // Overwrite, Unicode omitted
        dlr { writer?WriteLine("written") }
        dlr { writer?Close() }
        let reader: obj = dlr { return fso?OpenTextFile(path) }            // IOMode, Create, Format omitted
        let text: string = dlr { return reader?ReadAll() }
        dlr { reader?Close() }
        dlr { fso?CopyFile(path, Path.Combine(dir, "d.txt")) }             // OverWriteFiles omitted
        text |> should equal ("written" + Environment.NewLine)
        File.Exists(Path.Combine(dir, "d.txt")) |> should equal true
        let rs = recordset ()
        let count: int = dlr { return rs?RecordCount }
        count |> should equal 3)

[<Fact>]
let ``an optional COM argument passed by name`` () =
    let fso = create "Scripting.FileSystemObject"
    withFolder (fun dir ->
        let path = Path.Combine(dir, "new.txt")
        let stream: obj = dlr { return fso?OpenTextFile(path, Dlr.named {| Create = true |}) }   // IOMode skipped, Create named
        dlr { stream?Close() }
        File.Exists path |> should equal true)

[<Fact>]
let ``COM events: addAssign and subtractAssign with a delegate, as C#'s += and -=`` () =
    let rs = recordset ()
    let moves = ref 0
    let handler = Action<obj, obj, obj, obj>(fun _ _ _ _ -> moves.Value <- moves.Value + 1)
    dlr { rs |> Dlr.addAssign "MoveComplete" handler }
    dlr { rs?MoveFirst() }
    let seen = moves.Value
    dlr { rs |> Dlr.subtractAssign "MoveComplete" handler }
    dlr { rs?MoveLast() }
    seen |> should be (greaterThan 0)
    moves.Value |> should equal seen                                       // unsubscribed: the second move is not seen
    seen |> should equal (Tests.CSharp.CSharpComEvents.MovesSeen(recordset ()))

[<Fact>]
let ``COM events: an F# function as the handler`` () =
    let rs = recordset ()
    let moves = ref 0
    let handler (_: obj) (_: obj) (_: obj) (_: obj) = moves.Value <- moves.Value + 1
    dlr { rs |> Dlr.addAssign "MoveComplete" handler }
    dlr { rs?MoveFirst() }
    moves.Value |> should be (greaterThan 0)

[<Fact>]
let ``a Dlr.ref argument reaches a COM method as a by-reference VARIANT`` () =
    // No COM server on stock (64-bit) Windows has an [in, out] parameter to write back through,
    // so this pins the half that can be tested: a Dlr.ref goes through C#'s COM binder as a
    // VT_BYREF VARIANT, which ADO's ReadText ([in] long NumChars) coerces, and its value comes back.
    let stream = create "ADODB.Stream"
    dlr { stream?Type <- 2 }                                   // adTypeText
    dlr { stream?Open() }
    dlr { stream?WriteText("Testing dlr COM interop") }
    dlr { stream?Position <- 0 }
    let mutable count = -1                                     // adReadAll
    let text: string = dlr { return stream?ReadText(Dlr.ref count) }
    dlr { stream?Close() }
    text |> should equal "Testing dlr COM interop"
    count |> should equal -1                                   // an [in] parameter: back as it went

[<Fact>]
let ``a Dlr.ref argument meets a by-reference COM parameter (Scripting.Dictionary)`` () =
    // Dictionary declares its keys `[in] VARIANT*`: by reference in the signature, input only. A
    // Dlr.ref there is the declared shape itself rather than one the server coerces (ADODB above);
    // the server reads the key and writes nothing back.
    let dict = create "Scripting.Dictionary"
    dlr { dict?Add("k", "v") }
    let mutable key = "k"
    let found: bool = dlr { return dict?Exists(Dlr.ref key) }
    let mutable missing = "nope"
    let absent: bool = dlr { return dict?Exists(Dlr.ref missing) }
    (found, absent) |> should equal (true, false)
    (key, missing) |> should equal ("k", "nope")

/// An ADO connection to SQL Server LocalDB (on the Windows CI runner: MSOLEDBSQL19 / MSOLEDBSQL),
/// retried while LocalDB starts; skipped where none opens.
let private localDb () : obj =
    let candidates =
        [ "Provider=MSOLEDBSQL19;Data Source=(localdb)\\MSSQLLocalDB;Integrated Security=SSPI;Use Encryption for Data=Optional"
          "Provider=MSOLEDBSQL;Data Source=(localdb)\\MSSQLLocalDB;Integrated Security=SSPI" ]
    let attempt (cs: string) =
        try
            let conn = create "ADODB.Connection"
            dlr { conn?Open(cs) }
            Some conn
        with :? AnyUnit.IgnoreException -> reraise () | _ -> None
    // A cold LocalDB can time out the first login: try each provider, then once more.
    match (candidates @ candidates) |> List.tryPick attempt with
    | Some conn -> conn
    | None -> raise (AnyUnit.IgnoreException "no ADO provider opens SQL Server LocalDB here")

[<Fact>]
let ``a COM [out] parameter is written back through Dlr.out, as C#'s out (ADO Connection.Execute)`` () =
    // Connection.Execute(CommandText, [out] RecordsAffected, Options): the server writes the count.
    let conn = localDb ()
    try
        dlr { conn?Execute("CREATE TABLE #t (x int); INSERT INTO #t VALUES (1), (2), (3)") }
        let (_: obj), (affected: obj) = dlr { return conn?Execute("UPDATE #t SET x = x + 1", Dlr.out) }
        let struct (_: obj, again: int) = dlr { return conn?Execute("UPDATE #t SET x = x + 1 WHERE x > 2", Dlr.out) }
        affected |> should equal (box 3)
        again |> should equal 2
        affected |> should equal (Tests.CSharp.CSharpComEvents.RecordsAffected(conn, "UPDATE #t SET x = x + 1"))
    finally
        dlr { conn?Close() }
