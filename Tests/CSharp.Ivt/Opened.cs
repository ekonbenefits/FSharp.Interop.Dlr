using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Tests")]
[assembly: InternalsVisibleTo("Tests.Wasm")]

namespace Tests.CSharp.Ivt
{
    /// <summary>An <c>internal</c> class whose assembly opens its internals to the tests.</summary>
    internal class Opened
    {
        public int Value => 7;
        public Func<int, int> Fn = x => x * 2;
        public int Plain(int x) => x + 1;
    }

    public static class Make
    {
        public static object Opened() => new Opened();
    }
}
