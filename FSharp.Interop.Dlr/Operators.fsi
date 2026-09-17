namespace FSharp.Interop.Dlr

/// <summary>Result of <c>Dlr.named</c>; only meaningful inside <c>dlr { }</c>.</summary>
[<Sealed>]
type Named<'T> =
    class end

/// <summary>Result of <c>Dlr.typeArgs</c>; only meaningful inside <c>dlr { }</c>.</summary>
[<Sealed>]
type TypeArgs =
    class end

/// <summary>Result of <c>Dlr.idx</c>; only meaningful inside <c>dlr { }</c>. Index with <c>.[i]</c>, <c>.[i, j]</c>, … up to four indexes.</summary>
[<Sealed>]
type Indexed<'T> =
    member Item: i: obj -> 'T with get, set
    member Item: i: obj * j: obj -> 'T with get, set
    member Item: i: obj * j: obj * k: obj -> 'T with get, set
    member Item: i: obj * j: obj * k: obj * l: obj -> 'T with get, set

/// <summary>
/// Operators recognised inside <c>dlr { }</c>. They are never executed: the builder inspects the
/// quotation and compiles each one to a DLR call site. Calling any of them outside <c>dlr { }</c> throws.
/// </summary>
[<AutoOpen>]
module Operators =
    /// <summary>Dynamic member get, or member invoke when applied: <c>x?Foo</c>, <c>x?Foo(a, b)</c>.</summary>
    val ( ? ) : target:obj -> name:string -> 'TResult
    /// <summary>Dynamic member set: <c>x?Foo &lt;- v</c>.</summary>
    val ( ?<- ) : target:obj -> name:string -> value:'TValue -> unit
    /// <summary>Dynamic modulo.</summary>
    val ( ?%? ) : left:obj -> right:obj -> 'TResult
    /// <summary>Dynamic multiply.</summary>
    val ( ?*? ) : left:obj -> right:obj -> 'TResult
    /// <summary>Dynamic add (also string concatenation).</summary>
    val ( ?+? ) : left:obj -> right:obj -> 'TResult
    /// <summary>Dynamic subtract.</summary>
    val ( ?-? ) : left:obj -> right:obj -> 'TResult
    /// <summary>Dynamic divide.</summary>
    val ( ?/? ) : left:obj -> right:obj -> 'TResult
    /// <summary>Dynamic bitwise and.</summary>
    val ( ?&&&? ) : left:obj -> right:obj -> 'TResult
    /// <summary>Dynamic bitwise or.</summary>
    val ( ?|||? ) : left:obj -> right:obj -> 'TResult
    /// <summary>Dynamic bitwise xor.</summary>
    val ( ?^^^? ) : left:obj -> right:obj -> 'TResult
    /// <summary>Dynamic left shift.</summary>
    val ( ?<<<? ) : left:obj -> right:obj -> 'TResult
    /// <summary>Dynamic right shift.</summary>
    val ( ?>>>? ) : left:obj -> right:obj -> 'TResult
    /// <summary>Dynamic less-or-equal.</summary>
    val ( ?<=? ) : left:obj -> right:obj -> bool
    /// <summary>Dynamic not-equal.</summary>
    val ( ?<>? ) : left:obj -> right:obj -> bool
    /// <summary>Dynamic less-than.</summary>
    val ( ?<? ) : left:obj -> right:obj -> bool
    /// <summary>Dynamic equal.</summary>
    val ( ?=? ) : left:obj -> right:obj -> bool
    /// <summary>Dynamic greater-than.</summary>
    val ( ?>? ) : left:obj -> right:obj -> bool
    /// <summary>Dynamic greater-or-equal.</summary>
    val ( ?>=? ) : left:obj -> right:obj -> bool

/// <summary>
/// Markers recognised inside <c>dlr { }</c>. Like the operators they are never executed and throw if called directly.
/// </summary>
[<Sealed; AbstractClass>]
type Dlr =
    /// <summary>Marks an anonymous record as named arguments: <c>x?Method(a, Dlr.named {| count = 3 |})</c>. A bare anonymous record is a positional argument.</summary>
    static member named: record: 'T -> Named<'T>
    /// <summary>Dynamic indexing: <c>(Dlr.idx x).[i]</c>, <c>(Dlr.idx x).[i, j] &lt;- v</c>. The element type is inferred from use.</summary>
    static member idx: target: obj -> Indexed<'T>
    /// <summary>Explicit generic type arguments for a member invocation: <c>x?Get(Dlr.typeArgs&lt;int&gt;())</c> calls <c>Get&lt;int&gt;()</c>. Must be the first argument.</summary>
    static member typeArgs<'A> : unit -> TypeArgs
    /// <summary>Two explicit generic type arguments.</summary>
    static member typeArgs<'A, 'B> : unit -> TypeArgs
    /// <summary>Three explicit generic type arguments.</summary>
    static member typeArgs<'A, 'B, 'C> : unit -> TypeArgs
    /// <summary>Four explicit generic type arguments.</summary>
    static member typeArgs<'A, 'B, 'C, 'D> : unit -> TypeArgs
    /// <summary>Explicit conversion, like a C# cast: <c>Dlr.cast&lt;int&gt; x?Ratio</c>. Results of <c>?</c> convert implicitly on their own.</summary>
    static member cast<'T> : value: obj -> 'T
    /// <summary>Pipe-friendly member get: <c>x |> Dlr.get "Name"</c>; chains as <c>x |> Dlr.get "A" |> Dlr.get "B"</c>, and applied it invokes, like <c>?</c>: <c>(x |> Dlr.get "Add") (1, 2)</c>. The name may be computed.</summary>
    static member get: name: string -> target: obj -> 'T
    /// <summary>Invoke the target itself (a delegate, a callable dynamic object): <c>f |> Dlr.call (a, b)</c>, <c>f |> Dlr.call ()</c>. Arguments follow the same rules as a member invocation.</summary>
    static member call: args: 'TArgs -> target: obj -> 'T
    /// <summary>Pipe-friendly member set: <c>x |> Dlr.set "Name" value</c>.</summary>
    static member set: name: string -> value: 'TValue -> target: obj -> unit
    /// <summary>Pipe-friendly invocation: <c>x |> Dlr.invoke "Add" (1, 2)</c>, <c>x |> Dlr.invoke "Touch" ()</c>; the arguments follow the same rules as <c>x?Add(1, 2)</c>, including <c>Dlr.named</c> and <c>Dlr.typeArgs</c>.</summary>
    static member invoke: name: string -> args: 'TArgs -> target: obj -> 'T
    /// <summary>Implicit conversion of a value to the type inferred from use (widening, <c>op_Implicit</c>, <c>TryConvert</c>): <c>let n: int64 = dlr { return Dlr.implicit x }</c>.</summary>
    static member implicit: value: obj -> 'T
    /// <summary>Dynamic unary minus.</summary>
    static member neg: value: obj -> 'TResult
    /// <summary>Dynamic logical not.</summary>
    static member not: value: obj -> 'TResult
    /// <summary>Dynamic bitwise complement.</summary>
    static member complement: value: obj -> 'TResult
