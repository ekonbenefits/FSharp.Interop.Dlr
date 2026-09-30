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
    }

    /// <summary>A dynamic object with an out parameter: it writes the value into <c>args</c>, which the DLR carries back.</summary>
    public class DynamicOuts : DynamicObject
    {
        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
        {
            if (binder.Name == "TryHalf")
            {
                var n = (int)args[0];
                args[1] = n / 2;
                result = n % 2 == 0;
                return true;
            }
            result = null!;
            return false;
        }
    }
}
