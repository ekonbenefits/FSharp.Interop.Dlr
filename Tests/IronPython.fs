/// IronPython 3: the DLR's own language, so every Python object with a namespace — a scope, a
/// module, a class, a function, an instance — is a native IDynamicMetaObjectProvider with the
/// reference meta-object semantics: a class is callable, a scope grows variables, `**kwargs` are
/// named arguments, `__eq__` / `__add__` / `__call__` answer the operator, equality and invoke
/// binders. (Dicts and lists are plain CLR classes with indexers; ints and strings arrive as CLR
/// values.) Python.NET's objects are a DynamicObject subclass answering the fallbacks instead.
/// Pure managed, no runtime to find.
[<ReflectedDefinition>]
module Tests.IronPython

open System
open AnyUnit.Style.Xunit
open AnyUnit.Style.FsUnit
open FSharp.Interop.Dlr
open Microsoft.CSharp.RuntimeBinder

let private source = """
import math

class Point:
    def __init__(self, x, y=0):
        self.x = x
        self.y = y
    def scaled(self, k=2):
        return Point(self.x * k, self.y * k)
    def __repr__(self):
        return "Point(%d, %d)" % (self.x, self.y)
    def __add__(self, other):
        return Point(self.x + other.x, self.y + other.y)
    def __eq__(self, other):
        return self.x == other.x and self.y == other.y
    def __call__(self, k):
        return Point(self.x * k, self.y * k)

def add(a, b):
    return a + b

def greet(name, greeting="hello", punct="!"):
    return greeting + " " + name + punct

def make_adder(n):
    return lambda x: x + n

data = {"name": "py", "items": [1, 2, 3]}
"""

/// One engine (thread-safe for scope creation); a fresh scope with the module above per test,
/// the target of every block, an `obj` like any other.
let private engine = lazy IronPython.Hosting.Python.CreateEngine()
let private scope () : obj =
    let scope = engine.Value.CreateScope()
    engine.Value.Execute(source, scope) |> ignore
    box scope

[<Fact>]
let ``module functions: positional, keyword and data-driven arguments, duck typing`` () =
    let m = scope ()
    let ints: int = dlr { return m?add(2, 3) }
    let strings: string = dlr { return m?add("a", "b") }                 // the same function, Python's typing
    ints |> should equal 5
    strings |> should equal "ab"
    let plain: string = dlr { return m?greet("jay") }
    let keyword: string = dlr { return m?greet("jay", Dlr.named {| punct = "?" |}) }      // by name: positional would be "? jay!"
    plain |> should equal "hello jay!"
    keyword |> should equal "hello jay?"
    let greet (kwargs: (string * obj) list) : string = dlr { return m?greet("jay", Dlr.namedOf kwargs) }
    greet [ "punct", box "?"; "greeting", box "yo" ] |> should equal "yo jay?"
    let splat (args: obj list) : int = dlr { return m?add(Dlr.argsOf args) }
    splat [ box 2; box 3 ] |> should equal 5

[<Fact>]
let ``objects: a class is callable, attributes read and write, methods take defaults`` () =
    let m = scope ()
    let p: obj = dlr { return m?Point(3, 4) }
    let x: int = dlr { return p?x }
    x |> should equal 3
    dlr { p?y <- 10 }
    let y: int = dlr { return p?y }
    y |> should equal 10
    let defaultK: int = dlr { return p?scaled()?x }
    let positionalK: int = dlr { return p?scaled(3)?y }
    defaultK |> should equal 6
    positionalK |> should equal 30
    let repr: string = dlr { return p?__repr__() }
    repr |> should equal "Point(3, 10)"

[<Fact>]
let ``callables through Dlr.apply, dicts and lists through Dlr.item, a module through the scope`` () =
    let m = scope ()
    let add5: obj = dlr { return m?make_adder(5) }
    let added: int = dlr { return add5 |> Dlr.apply 10 }
    added |> should equal 15
    let data: obj = dlr { return m?data }
    let name: string = dlr { return data |> Dlr.item "name" }
    let third: int = dlr { return data |> Dlr.item "items" |> Dlr.item 2 }
    name |> should equal "py"
    third |> should equal 3
    dlr { data |> Dlr.setItem "name" "changed" }
    let changed: string = dlr { return data |> Dlr.item "name" }
    changed |> should equal "changed"
    let root: float = dlr { return m?math?sqrt(16.0) }
    root |> should equal 4.0

[<Fact>]
let ``the meta-object answers the operator, equality and invoke binders: __add__, __eq__, __call__`` () =
    // Paths no fallback-based target reaches: Python's own operator and equality, and an instance
    // that is callable — and `?=?` defers to the object's equality rather than our structural rule.
    let m = scope ()
    let p1: obj = dlr { return m?Point(1, 2) }
    let p2: obj = dlr { return m?Point(3, 4) }
    let sumX: int = dlr { return (p1 ?+? p2)?x }
    sumX |> should equal 4
    let same: bool = dlr { return p1 ?=? m?Point(1, 2) }
    let different: bool = dlr { return p1 ?<>? p2 }
    same |> should equal true
    different |> should equal true
    let tripled: int = dlr { return (p1 |> Dlr.apply 3)?y }                  // __call__
    tripled |> should equal 6

[<Fact>]
let ``a scope and an instance are namespaces: setting a new name creates it`` () =
    let m = scope ()
    dlr { m?newvar <- 5 }
    let created: int = dlr { return m?newvar }
    created |> should equal 5
    let p: obj = dlr { return m?Point(1) }
    dlr { p?tag <- "t" }
    let tag: string = dlr { return p?tag }
    tag |> should equal "t"

[<Fact>]
let ``misses and errors: a missing name is the binder's error, a Python error carries its type`` () =
    let m = scope ()
    (fun () -> (dlr { return m?nothere } : obj) |> ignore) |> should throw typeof<RuntimeBinderException>
    let p: obj = dlr { return m?Point(1) }
    (fun () -> (dlr { return p?nothere } : obj) |> ignore) |> should throw typeof<RuntimeBinderException>   // the meta-object declines, C#'s binder reports
    (fun () -> (dlr { return m?add(1) } : int) |> ignore) |> should throw typeof<Microsoft.Scripting.ArgumentTypeException>   // Python's TypeError

[<Fact>]
let ``one site alternates IronPython objects and CLR objects`` () =
    let m = scope ()
    let py: obj = dlr { return m?Point(1, 2) }
    let targets: obj list = [ py; box {| x = 7 |}; py ]
    let xs = ResizeArray<int>()
    dlr {
        for t in targets do
            xs.Add(t?x)
    }
    List.ofSeq xs |> should equal [ 1; 7; 1 ]
