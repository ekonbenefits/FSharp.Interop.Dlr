using System.Dynamic;

namespace Tests.CSharp
{
    /// <summary>Methods with <c>ref</c> and <c>out</c> parameters, for <c>Dlr.out</c> / <c>Dlr.ref</c> (#131).</summary>
    public class ByRefs
    {
        public void Swap(ref int a, ref int b) { var t = a; a = b; b = t; }
        public int DivRem(int a, int b, out int remainder) { remainder = a % b; return a / b; }
        public void Split(string s, out string left, out string right)
        {
            var i = s.IndexOf(',');
            left = s.Substring(0, i);
            right = s.Substring(i + 1);
        }
        public void Twice(ref string s) { s = s + s; }
        public bool TryHalf(int n, out int half) { half = n / 2; return n % 2 == 0; }
        public int Scale(int n, out int remainder, int by = 2) { remainder = n % by; return n / by; }
        public string Concat(int n, ref string s) { s = s + n; return s; }
        public void Prepend(ref string s, int n) { s = s + n; }
        public void Halve(int n, out int half) { half = n / 2; }
        public void Eight(out int a, out int b, out int c, out int d, out int e, out int f, out int g, out int h)
        { a = 1; b = 2; c = 3; d = 4; e = 5; f = 6; g = 7; h = 8; }
        public int AddBoth(ref int a, ref int b) { a++; b++; return a + b; }

        /// <summary>C# <c>dynamic</c> with the same variable passed by ref twice: one storage.</summary>
        public static (int, int) CSharpSameRefTwice(object o)
        {
            dynamic d = o;
            var x = 0;
            int r = d.AddBoth(ref x, ref x);
            return (r, x);
        }

        /// <summary>C# <c>dynamic</c>'s own order for a ref before an argument that writes the same
        /// variable: a ref is a reference, so the callee sees the later write.</summary>
        public static string CSharpRefThenWrite(object o)
        {
            dynamic d = o;
            var s = "a";
            int Bump() { s += "b"; return 1; }
            d.Prepend(ref s, Bump());
            return s;
        }
        public object? Nothing() => null;
        public static TryHalfFn HalfFn => (int n, out int half) => { half = n / 2; return n % 2 == 0; };
    }

    /// <summary>A delegate with an out parameter, for <c>Dlr.call</c> / <c>Dlr.apply</c>.</summary>
    public delegate bool TryHalfFn(int n, out int half);

    /// <summary>A constructor with a ref parameter, for <c>Dlr.new'</c>.</summary>
    public class Counted
    {
        public Counted(string name, ref int count) { count++; Name = name; }
        public string Name { get; }
    }

    /// <summary>A generic method with an out parameter, for type arguments.</summary>
    public class Generic
    {
        public bool TryDefault<T>(out T value) { value = default!; return true; }
    }

    /// <summary>A dynamic object with an out parameter: it writes the value into <c>args</c>, which the DLR carries back.</summary>
    public class DynamicOuts : DynamicObject
    {
        public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
        {
            if (binder.Name == "TryHalf")
            {
                var n = (int)args![0]!;
                args[1] = n / 2;
                result = n % 2 == 0;
                return true;
            }
            result = null;
            return false;
        }
    }
}
