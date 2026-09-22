/// Python.NET: every Python object is a DynamicObject — attribute get/set, calls with positional
/// and keyword arguments, indexing, conversion — and Python's own duck typing. Skipped when no
/// Python runtime is found (PYTHONNET_PYDLL, else asked of `python3`).
[<ReflectedDefinition>]
module Tests.PythonNet

open System
open System.Diagnostics
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Python.Runtime
open Microsoft.CSharp.RuntimeBinder

/// The shared library a Python 3.10+ would load (pythonnet 3.1 supports 3.10-3.14), or None:
/// PYTHONNET_PYDLL if set, else asked of the first `python3.x`/`python3` on PATH that qualifies.
let private findPythonDll () =
    match Environment.GetEnvironmentVariable "PYTHONNET_PYDLL" with
    | dll when not (String.IsNullOrEmpty dll) -> Some dll
    | _ ->
        let script =
            "import sys, sysconfig, os; v=sysconfig.get_config_vars(); " +
            "c=[os.path.join(v.get('LIBDIR') or '', v.get('INSTSONAME') or ''), os.path.join(v.get('LIBDIR') or '', v.get('LDLIBRARY') or ''), " +
            "os.path.join(v.get('PYTHONFRAMEWORKPREFIX') or '', v.get('LDLIBRARY') or ''), os.path.join(sys.base_prefix, 'Python3'), " +
            "os.path.join(sys.base_prefix, 'python%d%d.dll' % sys.version_info[:2])]; " +
            "print(next((x for x in c if os.path.isfile(x)), '') if sys.version_info >= (3, 10) else '')"
        let ask (exe: string) =
            try
                let psi = ProcessStartInfo(exe, "-c \"" + script + "\"", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
                use p = Process.Start psi
                let dll = p.StandardOutput.ReadToEnd().Trim()
                p.WaitForExit()
                if p.ExitCode = 0 && dll <> "" && IO.File.Exists dll then Some dll else None
            with _ -> None
        [ "python3.14"; "python3.13"; "python3.12"; "python3.11"; "python3.10"; "python3"; "python" ] |> List.tryPick ask

let private python =
    lazy
        (match findPythonDll () with
         | None -> None
         | Some dll ->
             try
                 Runtime.PythonDLL <- dll
                 PythonEngine.Initialize()
                 PythonEngine.BeginAllowThreads() |> ignore
                 Some dll
             with _ -> None)

let private requirePython () =
    match python.Value with
    | Some _ -> ()
    | None -> raise (AnyUnit.IgnoreException "no Python runtime found for Python.NET (set PYTHONNET_PYDLL)")

let private source = """
import math

def add(a, b):
    return a + b

def greet(name, greeting="hello", punct="!"):
    return greeting + " " + name + punct

class Point:
    def __init__(self, x, y=0):
        self.x = x
        self.y = y
    def scaled(self, k=2):
        return Point(self.x * k, self.y * k)
    def __repr__(self):
        return "Point(%d, %d)" % (self.x, self.y)

def make_adder(n):
    return lambda x: x + n

def apply(f, x):
    return f(x)

data = {"name": "py", "items": [1, 2, 3]}
"""

[<Fact>]
let ``Python module functions, positional and keyword arguments`` () =
    requirePython ()
    use _gil = Py.GIL()
    let m: obj = PyModule.FromString("sample", source)
    let ints: int = dlr { return m?add(2, 3) }
    let floats: float = dlr { return m?add(2.5, 3.0) }                        // duck typing: same function
    let strings: string = dlr { return m?add("a", "b") }
    ints |> should equal 5
    floats |> should equal 5.5
    strings |> should equal "ab"
    let plain: string = dlr { return m?greet("jay") }
    let oneKeyword: string = dlr { return m?greet("jay", Dlr.named {| greeting = "hi" |}) }
    let twoKeywords: string = dlr { return m?greet("jay", Dlr.named {| punct = "?"; greeting = "yo" |}) }
    plain |> should equal "hello jay!"
    oneKeyword |> should equal "hi jay!"
    twoKeywords |> should equal "yo jay?"
    // Keyword arguments from data: Dlr.namedOf, compiled once per distinct name list.
    let greet (kwargs: (string * obj) list) : string = dlr { return m?greet("jay", Dlr.namedOf kwargs) }
    greet [ "punct", box "?"; "greeting", box "yo" ] |> should equal "yo jay?"
    greet [ "greeting", box "hi" ] |> should equal "hi jay!"
    // The function name from data too.
    let call (fname: string) (kwargs: (string * obj) list) : string = dlr { return (?) m fname ("jay", Dlr.namedOf kwargs) }
    call "greet" [ "punct", box "?"; "greeting", box "yo" ] |> should equal "yo jay?"
    // f(*args, **kwargs): both lists from data.
    let splat (args: obj list) (kwargs: (string * obj) list) : string = dlr { return m?greet(Dlr.argsOf args, Dlr.namedOf kwargs) }
    splat [ box "jay" ] [ "punct", box "?"; "greeting", box "yo" ] |> should equal "yo jay?"
    let add (args: obj list) : int = dlr { return m?add(Dlr.argsOf args) }
    add [ box 2; box 3 ] |> should equal 5

[<Fact>]
let ``Python objects: attributes, methods, defaults, repr`` () =
    requirePython ()
    use _gil = Py.GIL()
    let m: obj = PyModule.FromString("sample2", source)
    let p: obj = dlr { return m?Point(3, 4) }                                  // a class is callable
    let x: int = dlr { return p?x }
    x |> should equal 3
    dlr { p?y <- 10 }
    let y: int = dlr { return p?y }
    y |> should equal 10
    let defaultK: int = dlr { return p?scaled()?x }                            // default k
    let positionalK: int = dlr { return p?scaled(3)?y }
    let keywordK: int = dlr { return p?scaled(Dlr.named {| k = 10 |})?x }
    defaultK |> should equal 6
    positionalK |> should equal 30
    keywordK |> should equal 30
    let repr: string = dlr { return p?__repr__() }
    repr |> should equal "Point(3, 10)"

[<Fact>]
let ``Python callables through Dlr.apply, dicts and lists through Dlr.item`` () =
    requirePython ()
    use _gil = Py.GIL()
    let m: obj = PyModule.FromString("sample3", source)
    let add5: obj = dlr { return m?make_adder(5) }
    let added: int = dlr { return add5 |> Dlr.apply 10 }
    added |> should equal 15
    let data: obj = dlr { return m?data }
    let name: string = dlr { return data |> Dlr.item "name" }
    let third: int = dlr { return data |> Dlr.item "items" |> Dlr.item 2 }
    name |> should equal "py"
    third |> should equal 3
    // PyObject has no TrySetIndex; its CLR indexer's setter takes a PyObject, so the value is
    // converted first (as with Python.NET's own `dynamic` use).
    dlr { data |> Dlr.setItem "name" ("changed".ToPython()) }
    let changed: string = dlr { return data |> Dlr.item "name" }
    changed |> should equal "changed"
    let root: float = dlr { return m?math?sqrt(16.0) }                         // an imported module through the module
    root |> should equal 4.0

[<Fact>]
let ``an F# function passed to Python is called from Python`` () =
    requirePython ()
    use _gil = Py.GIL()
    let m: obj = PyModule.FromString("sample6", source)
    let applied: int = dlr { return m?apply((fun (x: int) -> x + 1), 41) }
    applied |> should equal 42

[<Fact>]
let ``Python errors and misses`` () =
    requirePython ()
    use _gil = Py.GIL()
    let m: obj = PyModule.FromString("sample4", source)
    // A missing attribute is Python.NET's KeyNotFoundException (its TryGetMember's choice, not the
    // binder's); a Python-side error is a PythonException carrying the Python type.
    (fun () -> (dlr { return m?nothere() } : int) |> ignore) |> should throw typeof<Collections.Generic.KeyNotFoundException>
    let ex = AnyUnit.Run.Assert.Current.Throws<PythonException>(fun () -> (dlr { return m?add(1) } : int) |> ignore)
    ex.Type.Name |> should equal "TypeError"

[<Fact>]
let ``one site alternates Python objects and CLR objects`` () =
    requirePython ()
    use _gil = Py.GIL()
    let m: obj = PyModule.FromString("sample5", source)
    let py: obj = dlr { return m?Point(1, 2) }
    let targets: obj list = [ py; box {| x = 7 |}; py ]
    let xs = ResizeArray<int>()
    dlr {
        for t in targets do
            xs.Add(t?x)
    }
    List.ofSeq xs |> should equal [ 1; 7; 1 ]
