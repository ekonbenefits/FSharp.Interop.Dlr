using System;
using System.Collections.Generic;

namespace FSharp.Interop.Dlr.Benchmarks.CSharp;

/// <summary>What the C# compiler emits for <c>dynamic</c>: one static call site per operation,
/// the same Microsoft.CSharp binders the F# blocks use. Static methods so the F# suite can call
/// them and list them in the same table.</summary>
public static class CSharpDynamic
{
    public static int Get(object o) { dynamic d = o; return d.Count; }

    public static int Call(object o, int i) { dynamic d = o; return d.Add(i, 1); }

    public static void Set(object o) { dynamic d = o; d.Name = "n"; }

    public static int Loop(object o, List<int> items)
    {
        dynamic d = o;
        var s = 0;
        foreach (var x in items) s += (int)d.Add(x, 1);
        return s;
    }

    public static int FunctionMember(object o) { dynamic d = o; return d.Fn(1, 2); }   // fails: FSharpFunc is not invocable in C#

    public static int OptionalOmitted(object o) { dynamic d = o; return d.Bump(1); }   // fails: C# cannot omit ?step

    public static int RunFunc(object o) { dynamic d = o; return d.Run(new Func<int, int>(x => x + 1)); }

    public static int StaticOverloads(object o) { return Widget.Draw((dynamic)o); }

    public static object Construct() { dynamic w = new Widget(); return w; }

    public static bool AreEqual(object a, object b) { dynamic x = a; dynamic y = b; return x == y; }

    public static int JObjectGet(object j) { dynamic d = j; return d.count; }

    public static string JObjectChain(object j) { dynamic d = j; return d.owner.name; }

    public static int ExpandoGet(object e) { dynamic d = e; return d.count; }
}

public class Widget
{
    public int Count { get; set; } = 3;
    public string Name { get; set; } = "widget";
    public int Add(int a, int b) => a + b;
    public int Run(Func<int, int> f) => f(21);
    public static int Draw(object o) => 1;
    public static int Draw(Widget w) => 2;
}
