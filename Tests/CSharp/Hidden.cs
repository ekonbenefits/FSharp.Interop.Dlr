using System;

namespace Tests.CSharp
{
    /// <summary>An <c>internal</c> class: its public members are invisible to C# <c>dynamic</c> from another assembly.</summary>
    internal class Hidden
    {
        public int Value => 7;
        public Func<int, int> Fn = x => x * 2;
        public int Plain(int x) => x + 1;
        public int Opt(int x, int step = 1) => x + step;
    }

    /// <summary>The same members on a public class.</summary>
    public class Shown
    {
        public int Value => 7;
        public Func<int, int> Fn = x => x * 2;
        public int Plain(int x) => x + 1;
        public int Opt(int x, int step = 1) => x + step;
    }

    /// <summary>A public generic: visible only when its argument is.</summary>
    public class Box<T>
    {
        public int Value => 7;
    }

    /// <summary>A generic outer with a protected nested type: its DeclaringType is the open <c>GOuter&lt;T&gt;</c>.</summary>
    public class GOuter<T>
    {
        protected class NestedProtected
        {
            public int Value => 7;
            public Func<int, int> Fn = x => x * 2;
        }

        protected object MakeNested() => new NestedProtected();
    }

    /// <summary>Protected instance members, for C#'s qualifier rule; a protected static, which has none.</summary>
    public class Outer<T>
    {
        protected int P => 5;
        protected Func<int, int> Q = x => x + 5;
        protected static int S => 6;
    }

    public class SiblingOfInt : Outer<int> { }

    public static class Make
    {
        public static object OuterOfInt() => new Outer<int>();
        public static object OuterOfString() => new Outer<string>();
        public static object Hidden() => new Hidden();
        public static object BoxOfHidden() => new Box<Hidden>();
        public static object BoxOfInt() => new Box<int>();
        public static object Shown() => new Shown();
        /// <summary>An anonymous type: internal to its assembly.</summary>
        public static object Anonymous() => new { X = 7, Fn = new Func<int, int>(x => x * 2) };
    }
}
