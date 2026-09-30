/// PowerShell: a `PSObject` is an `IDynamicMetaObjectProvider` whose members are PowerShell's —
/// note properties, script methods run with `$this`, and the adapted members of a wrapped .NET
/// object — so every form below binds through PowerShell's own binders, as C# `dynamic` does.
/// A script method's result is a `PSObject` around the value, and PowerShell's meta-object does
/// not unwrap it on conversion: `int x = d.Twice()` fails in C# too, so the result is read as
/// `obj` and unwrapped (`BaseObject`), as a C# caller would.
/// The engine package alone, no cmdlet modules: the objects are built with language features
/// (`[pscustomobject]`, `.psobject.Members.Add`). Skipped where the engine cannot start.
[<ReflectedDefinition>]
module Tests.PowerShell

open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open System.Management.Automation
open System.Management.Automation.Runspaces

/// Runs a test against a runspace of the engine alone (`CreateDefault2`: no snap-ins, no cmdlet
/// modules), open for the whole test and the calling thread's default: a script method runs when
/// the block calls it, on this thread. `run` returns the first object a script outputs, as the
/// `PSObject` PowerShell hands back.
let private withPowerShell (test: (string -> obj) -> unit) =
    let runspace =
        try
            let r = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2())
            r.Open()
            r
        with e -> raise (AnyUnit.IgnoreException ("PowerShell's engine could not start: " + e.Message))
    let previous = Runspace.DefaultRunspace
    Runspace.DefaultRunspace <- runspace
    try
        test (fun script ->
            use ps = PowerShell.Create()
            ps.Runspace <- runspace
            box (Seq.head (ps.AddScript(script).Invoke())))
    finally
        Runspace.DefaultRunspace <- previous
        runspace.Dispose()

/// A script's value out of the `PSObject` PowerShell wraps it in.
let private unwrap (o: obj) : obj =
    match o with
    | :? PSObject as p -> p.BaseObject
    | o -> o

let private widgetScript = """
    $o = [pscustomobject]@{ Name = 'ps'; Count = 3 }
    $o.psobject.Members.Add([psscriptmethod]::new('Twice', { $this.Count * 2 }))
    $o.psobject.Members.Add([psscriptmethod]::new('Greet', { param($who) "hi $who from $($this.Name)" }))
    $o.psobject.Members.Add([psscriptmethod]::new('Apply', { param($f) $f.Invoke($this.Count) }))
    $o
"""

[<Fact>]
let ``note properties with typed conversion`` () =
    withPowerShell (fun run ->
        let w = run widgetScript
        let name: string = dlr { return w?Name }
        let count: int = dlr { return w?Count }
        name |> should equal "ps"
        count |> should equal 3)

[<Fact>]
let ``script methods run with $this and take arguments`` () =
    withPowerShell (fun run ->
        let w = run widgetScript
        let twice = dlr { return w?Twice() } |> unwrap
        let greeting = dlr { return w?Greet("f#") } |> unwrap
        twice |> should equal (box 6)
        greeting |> should equal (box "hi f# from ps"))

[<Fact>]
let ``a script method's PSObject result does not convert, as in C#`` () =
    withPowerShell (fun run ->
        let w = run widgetScript
        (fun () -> (dlr { return w?Twice() } : int) |> ignore) |> should throw typeof<Microsoft.CSharp.RuntimeBinder.RuntimeBinderException>)

[<Fact>]
let ``an F# function passed to a script method is called from PowerShell`` () =
    withPowerShell (fun run ->
        let w = run widgetScript
        let applied = dlr { return w?Apply(fun (n: int) -> n + 1) } |> unwrap
        applied |> should equal (box 4))

[<Fact>]
let ``a note property set is seen by PowerShell`` () =
    withPowerShell (fun run ->
        let w = run widgetScript
        dlr { w?Name <- "changed" }
        let name: string = dlr { return w?Name }
        let greeting = dlr { return w?Greet("f#") } |> unwrap
        name |> should equal "changed"
        greeting |> should equal (box "hi f# from changed"))

[<Fact>]
let ``a wrapped .NET object's adapted members`` () =
    withPowerShell (fun run ->
        let date = run "[datetime]::new(2020, 1, 2)"
        let year: int = dlr { return date?Year }
        let next: int = dlr { return date?AddDays(1)?Day }
        year |> should equal 2020
        next |> should equal 3)

[<Fact>]
let ``one site serves a PSObject, a JObject and a CLR object`` () =
    withPowerShell (fun run ->
        let targets: obj list = [ run widgetScript; box (Newtonsoft.Json.Linq.JObject.Parse """{ "Name": "j" }"""); box (Widget()) ]
        let names = ResizeArray<string>()
        dlr {
            for t in targets do
                names.Add(t?Name)
        }
        List.ofSeq names |> should equal [ "ps"; "j"; "widget" ])
