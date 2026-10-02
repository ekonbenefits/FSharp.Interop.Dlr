namespace FSharp.Interop.Dlr

/// <summary>Result of <c>Dlr.named</c>; only meaningful inside <c>dlr { }</c>.</summary>
[<Sealed>]
type Named<'T> =
    class end

/// <summary>Result of <c>Dlr.typeArgs</c>; only meaningful inside <c>dlr { }</c>.</summary>
[<Sealed>]
type TypeArgs =
    class end

/// <summary>Type of <c>Dlr.out</c>; only meaningful inside <c>dlr { }</c>.</summary>
[<Sealed>]
type OutArg =
    class end

/// <summary>Result of <c>Dlr.ref</c>; only meaningful inside <c>dlr { }</c>.</summary>
[<Sealed>]
type RefArg<'T> =
    class end

/// <summary>
/// Operators recognised inside <c>dlr { }</c>. They are never executed: the builder inspects the
/// quotation and compiles each one to a DLR call site. Calling any of them outside <c>dlr { }</c> throws.
/// </summary>
[<AutoOpen>]
module Operators =
    /// <summary>Dynamic member get, or member invoke when applied: <c>x?Foo</c>, <c>x?Foo(a, b)</c>. A member holding an F# function value is applied; read as a function type (<c>let f: int -> int = dlr { return x?Foo }</c>) it is a curried invoker of the member.</summary>
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
    /// <summary>Marks a record literal, written in the call, as named arguments: <c>x?Method(a, Dlr.named {| count = 3 |})</c>. The names are read from the quotation, so the literal itself, not a record in a variable (names from data are <c>Dlr.namedOf</c>). A bare anonymous record is a positional argument.</summary>
    static member named: record: 'T -> Named<'T>
    /// <summary>Named arguments whose names are run-time values — keyword arguments from data: <c>plt?plot(xs, ys, Dlr.namedOf kwargs)</c> with <c>kwargs: (string * obj) list</c>. Values dispatch on their runtime types. The call is compiled once per distinct name list at the site (bounded), then cached; combines with positional arguments, <c>Dlr.named</c>, a computed member name and <c>Dlr.typeArgsOf</c>.</summary>
    static member namedOf: args: (string * obj) list -> Named<(string * obj) list>
    /// <summary>Positional arguments whose count is a run-time value — Python's <c>*args</c>: <c>m?f(Dlr.argsOf args, Dlr.namedOf kwargs)</c> is <c>f(*args, **kwargs)</c>. Values dispatch on their runtime types; compiled once per distinct count (and name list) at the site, like <c>Dlr.namedOf</c>; combines with fixed arguments before or after it and with <c>Dlr.named</c> / <c>Dlr.namedOf</c> after it. At most 64 values (<c>NamedOfCache.MaxPositional</c>; docs/syntax.md says why).</summary>
    static member argsOf: args: obj list -> Named<obj list>
    /// <summary>An <c>out</c> argument, returned as F# returns a method's out parameters: the result becomes a tuple, the return value first then each out in order — <c>let (found: bool), (v: int) = dlr { return d?TryGetValue("a", Dlr.out) }</c>, or a struct tuple, which allocates none: <c>let struct (found: bool, v: int) = …</c>. When the result has no slot for the return value (a void method, or a return you discard) it is the outs alone (the bare value for one). Each out's type is its element of the result type, which must be known where the block is used.</summary>
    static member out: OutArg
    /// <summary>An <c>out</c> argument whose type you state, rather than the result's shape inferring it: <c>Dlr.out</c> with its type written down. Needed where the shape is ambiguous — a lone out that is itself a tuple, read as the bare value: <c>let v: struct (int * int) = dlr { return o?PairOut(Dlr.outAs&lt;struct (int * int)&gt; ()) }</c>. The result follows the same shapes as <c>Dlr.out</c>, the first that agrees with every stated type.</summary>
    static member outAs<'T> : unit -> OutArg
    /// <summary>A <c>ref</c> argument over a <c>let mutable</c>: its value is read at the call (after every other argument) and the method's write lands back in it — <c>d?Swap(Dlr.ref a, Dlr.ref b)</c>. The same variable twice is one storage, as in C#; being copied back after the call, a method that throws leaves it unchanged.</summary>
    static member ref: variable: 'T -> RefArg<'T>
    /// <summary>Dynamic indexer get, target last: <c>x |&gt; Dlr.item i</c>, <c>x |&gt; Dlr.item (i, j)</c> (a tuple, literal or in a variable, is several indexes, as a tuple is several arguments in a member call; a struct tuple is one); the element type is inferred from use.</summary>
    static member item: indexes: 'TIndexes -> target: obj -> 'T
    /// <summary>Dynamic indexer set, target last: <c>x |&gt; Dlr.setItem i v</c>, <c>x |&gt; Dlr.setItem (i, j) v</c>.</summary>
    static member setItem: indexes: 'TIndexes -> value: 'TValue -> target: obj -> unit
    /// <summary>Explicit generic type arguments for a member invocation: <c>x?Get(Dlr.typeArgs&lt;int&gt;())</c> calls <c>Get&lt;int&gt;()</c>. Must be the first argument.</summary>
    static member typeArgs<'A> : unit -> TypeArgs
    /// <summary>Two explicit generic type arguments.</summary>
    static member typeArgs<'A, 'B> : unit -> TypeArgs
    /// <summary>Three explicit generic type arguments.</summary>
    static member typeArgs<'A, 'B, 'C> : unit -> TypeArgs
    /// <summary>Four explicit generic type arguments.</summary>
    static member typeArgs<'A, 'B, 'C, 'D> : unit -> TypeArgs
    /// <summary>Explicit generic type arguments as a list: <c>x?M(Dlr.typeArgsOf [ typeof&lt;A&gt;; typeof&lt;B&gt;; … ])</c> for more than four, or <c>x?M(Dlr.typeArgsOf ts)</c> with types only known at run time (one set of call sites per distinct list, cached per site like a computed name). Must be the first argument.</summary>
    static member typeArgsOf: types: System.Type list -> TypeArgs
    /// <summary>Construct a <c>'T</c> with the constructor chosen at run time by the arguments, C#'s <c>new T(dynamicArg)</c>: <c>Dlr.new'&lt;Handler&gt;(shape)</c>, <c>Dlr.new'&lt;Point&gt;(1, 2)</c>, <c>Dlr.new'&lt;Widget&gt;()</c>. Arguments keep their static type and follow the same rules as a member invocation — a tuple variable is several, <c>Dlr.new'&lt;T&gt; args</c>; <c>Dlr.named</c>, <c>Dlr.namedOf</c> and <c>Dlr.argsOf</c> allowed; up to eight written out, up to 64 through <c>Dlr.argsOf</c>.</summary>
    static member new'<'T> : unit -> 'T
    /// <summary>Construct a <c>'T</c> from 1 argument.</summary>
    static member new'<'T> : a: obj -> 'T
    /// <summary>Construct a <c>'T</c> from 2 arguments.</summary>
    static member new'<'T> : a: obj * b: obj -> 'T
    /// <summary>Construct a <c>'T</c> from 3 arguments.</summary>
    static member new'<'T> : a: obj * b: obj * c: obj -> 'T
    /// <summary>Construct a <c>'T</c> from 4 arguments.</summary>
    static member new'<'T> : a: obj * b: obj * c: obj * d: obj -> 'T
    /// <summary>Construct a <c>'T</c> from 5 arguments.</summary>
    static member new'<'T> : a: obj * b: obj * c: obj * d: obj * e: obj -> 'T
    /// <summary>Construct a <c>'T</c> from 6 arguments.</summary>
    static member new'<'T> : a: obj * b: obj * c: obj * d: obj * e: obj * f: obj -> 'T
    /// <summary>Construct a <c>'T</c> from 7 arguments.</summary>
    static member new'<'T> : a: obj * b: obj * c: obj * d: obj * e: obj * f: obj * g: obj -> 'T
    /// <summary>Construct a <c>'T</c> from 8 arguments.</summary>
    static member new'<'T> : a: obj * b: obj * c: obj * d: obj * e: obj * f: obj * g: obj * h: obj -> 'T
    /// <summary>Explicit conversion, like a C# cast: <c>Dlr.cast&lt;int&gt; x?Ratio</c>. Results of <c>?</c> convert implicitly on their own.</summary>
    static member cast<'T> : value: obj -> 'T
    /// <summary>Pipe-friendly member get: <c>x |> Dlr.get "Name"</c>; chains as <c>x |> Dlr.get "A" |> Dlr.get "B"</c>, and applied it invokes, like <c>?</c>: <c>(x |> Dlr.get "Add") (1, 2)</c>. The name may be computed.</summary>
    static member get: name: string -> target: obj -> 'T
    /// <summary>The <c>?</c> of values — the target itself (a delegate, a callable dynamic object, or an F# function value): applied, it invokes, <c>Dlr.call f (a, b)</c>, <c>Dlr.call f ()</c>; read at a function type, it is that function, <c>let add: int -> int -> int = dlr { return Dlr.call f }</c>, invoking the target when applied (curried or tupled, any arity; <c>unit -> R</c> invokes with no arguments; an F# function of that type is returned as it is). Pipe-friendly: <c>x |> Dlr.call</c>.</summary>
    static member call: target: obj -> 'T
    /// <summary>Invoke the target itself, target last for pipelines — the twin of <c>Dlr.invoke</c> for values: <c>f |> Dlr.apply (a, b)</c>, <c>f |> Dlr.apply ()</c>. Arguments follow the same rules as a member invocation.</summary>
    static member apply: args: 'TArgs -> target: obj -> 'T
    /// <summary>C#'s <c>d.Name += v</c>: adds a handler to an event — an F# function converts to the event's delegate type, or for a dynamic object's (COM's) event to the delegate of its signature; a new delegate is made per conversion, so to remove it later with <c>Dlr.subtractAssign</c>, add a delegate and keep it — or reads, adds and writes back for anything else: <c>btn |> Dlr.addAssign "Click" handler</c>, <c>stats |> Dlr.addAssign "Count" 1</c>.</summary>
    static member addAssign: name: string -> value: 'TValue -> target: obj -> unit
    /// <summary>C#'s <c>d.Name -= v</c>: removes a handler from an event, or reads, subtracts and writes back for anything else.</summary>
    static member subtractAssign: name: string -> value: 'TValue -> target: obj -> unit
    /// <summary>Pipe-friendly member set: <c>x |> Dlr.set "Name" value</c>.</summary>
    static member set: name: string -> value: 'TValue -> target: obj -> unit
    /// <summary>Pipe-friendly invocation: <c>x |> Dlr.invoke "Add" (1, 2)</c>, <c>x |> Dlr.invoke "Touch" ()</c>; the arguments follow the same rules as <c>x?Add(1, 2)</c>, including <c>Dlr.named</c>, <c>Dlr.namedOf</c>, <c>Dlr.argsOf</c> and <c>Dlr.typeArgs</c>.</summary>
    static member invoke: name: string -> args: 'TArgs -> target: obj -> 'T
    /// <summary>Implicit conversion of a value to the type inferred from use (widening, <c>op_Implicit</c>, <c>TryConvert</c>): <c>let n: int64 = dlr { return Dlr.implicit x }</c>.</summary>
    static member implicit: value: obj -> 'T
    /// <summary>Dynamic unary minus.</summary>
    static member neg: value: obj -> 'TResult
    /// <summary>Dynamic logical not.</summary>
    static member not: value: obj -> 'TResult
    /// <summary>Dynamic bitwise complement.</summary>
    static member complement: value: obj -> 'TResult

/// <summary>Types under the <c>Dlr</c> name (a module beside the <c>Dlr</c> class).</summary>
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Dlr =
    /// <summary>The static overload set of <c>'T</c> as a call target, C#'s <c>T.Method(dynamicArg)</c>: <c>Dlr.Static&lt;Renderer&gt;.Overloads?Draw(shape)</c> picks the overload by <c>shape</c>'s runtime type (multiple dispatch); also <c>|&gt; Dlr.invoke</c>, computed names and <c>Dlr.typeArgs</c>. For calls only — a static property is <c>T.P</c> in plain F#. Only meaningful inside <c>dlr { }</c>.</summary>
    [<Sealed; AbstractClass>]
    type Static<'T> =
        static member Overloads: obj
