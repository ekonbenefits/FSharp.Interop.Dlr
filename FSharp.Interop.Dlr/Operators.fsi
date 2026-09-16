namespace FSharp.Interop.Dlr

/// <summary>Result of <c>Dlr.named</c>; only meaningful inside <c>dlr { }</c>.</summary>
[<Sealed>]
type Named<'T> =
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
    /// <summary>Dynamic invoke of the target itself (delegate, callable dynamic object): <c>(!?x)(a, b)</c>.</summary>
    val ( !? ) : target:obj -> 'TResult
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
module Dlr =
    /// <summary>Marks an anonymous record as named arguments: <c>x?Method(a, Dlr.named {| count = 3 |})</c>. A bare anonymous record is a positional argument.</summary>
    val named : record:'T -> Named<'T>
    /// <summary>Dynamic indexing: <c>(Dlr.idx x).[i]</c>, <c>(Dlr.idx x).[i, j] &lt;- v</c>. The element type is inferred from use.</summary>
    val idx : target:obj -> Indexed<'T>
