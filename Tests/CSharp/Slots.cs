using System;
using System.Collections.Generic;

namespace Tests.CSharp
{
    /// <summary>A mutable struct with delegate-typed slots: assigned through a box, C# mutates the
    /// box, and so must the conversion of an F# function into them (#153).</summary>
    public struct MutableSlot
    {
        public Func<int, int> Property { get; set; }
        public Func<int, int> Field;
        public int Number { get; set; }
        public void Store(Func<int, int> f) { Property = f; }
    }

    /// <summary>Slots typed <c>Delegate</c> itself (WinForms' <c>Control.Invoke</c> shape): an F#
    /// function there is the Func/Action of its signature, not C#'s op_Implicit Converter (#153).</summary>
    public class AbstractSlots
    {
        public Delegate? Property { get; set; }
        public Delegate? Field;
        public Dictionary<string, Delegate> Map { get; } = new Dictionary<string, Delegate>();
    }

    /// <summary>Structs within a struct, for in-place mutation through a field path and an
    /// argument that mutates the receiver (#162).</summary>
    public struct InnerPoint
    {
        public int X;
    }

    public struct OuterPoint
    {
        public InnerPoint Inner;
        public int N;
        public int Total { get; set; }
        public int Next() { N++; return N; }
    }
}
