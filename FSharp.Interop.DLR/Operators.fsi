namespace FSharp.Interop.DLR

/// <summary>
/// Marks the anonymous record it wraps as named arguments in a dynamic invocation:
/// <c>target?Method(a, Named {| count = 3 |})</c>. A bare anonymous record is a positional argument.
/// </summary>
type Named<'T> = Named of 'T

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
    /// <summary>Dynamic indexer get: <c>getIndex x 0</c> or <c>getIndex x (0, 1)</c>.</summary>
    val getIndex : target:obj -> indexes:'TIndex -> 'TResult
    /// <summary>Dynamic indexer set: <c>setIndex x (0, 1) v</c>.</summary>
    val setIndex : target:obj -> indexes:'TIndex -> value:'TValue -> unit

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
