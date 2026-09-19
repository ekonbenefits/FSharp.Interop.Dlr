# The pipeline

From `dlr { … }` in source to the delegate a call invokes, and what one call costs. Part of
[internals](internals.md).

## From source to delegate

```mermaid
flowchart TD
    src["<b>dlr { … }</b><br/>F# desugars to<br/>dlr.Run(dlr.Delay(fun () -> …), file, line)"]
    run["<b>Run</b> (Builder.fs)<br/>closure.GetType() is the site's key"]
    sites["<b>Sites&lt;'T&gt;</b> (Cache.fs)<br/>typed per result type: last-hit compare,<br/>then dictionary by closure type"]
    invoke["<b>compiled.Invoke(closure)</b><br/>field reads + one CallSite per operation"]
    cache["<b>DlrCache</b> (Cache.fs)<br/>closure Type → Compiled"]
    discover["<b>Discover</b> (Discover.fs)<br/>the block's body, from the enclosing member's<br/>[&lt;ReflectedDefinition&gt;] by file and line"]
    translate["<b>Translate.translate</b> (Translate.fs)<br/>normalize → Plumbing / Members / Captures<br/>→ quotation with CallSites baked in"]
    binders["<b>Binders</b> (Binders.fs)<br/>one CallSite per operation: C#'s binder<br/>wrapped by the F#-aware ones"]
    conv["<b>LeafExpressionConverter</b> (FSharp.Core)<br/>quotation → LINQ tree"]
    hoist["<b>SiteHoister</b> (Translate.fs)<br/>CallSite constants → locals per lambda"]
    compile["<b>Compile()</b><br/>Func&lt;obj, 'T&gt;"]

    src --> run --> sites
    sites -- hit --> invoke
    sites -- miss --> cache
    cache -- miss --> discover --> translate
    translate <--> binders
    translate --> conv --> hoist --> compile --> cache
    cache --> sites

    classDef entry fill:#e8f1ff,stroke:#4a78c2
    classDef compileOnce fill:#fff4e0,stroke:#c98a1b
    class src,run,sites,invoke entry
    class cache,discover,translate,binders,conv,hoist,compile compileOnce
```

Blue is every call; orange is the first call at a site (and again after `DlrCache.clear()`).

`Delay` returns the closure unevaluated. `Run` never executes it; it is the call site's identity
(its type) and the source of the captured values (its fields). The compiled delegate takes that
closure as its one parameter and reads the captured variables off its fields.

## One call on the hot path

```mermaid
sequenceDiagram
    participant U as user code
    participant R as dlr.Run
    participant S as Sites<'T>
    participant D as compiled delegate
    participant C as CallSite (DLR)

    U->>R: Run(closure, file, line)
    Note over U,R: F# allocated the Delay closure (~3 ns)
    R->>S: Get(closure, …)
    S->>S: closure.GetType() (~3 ns)
    S->>S: last-hit: reference compare on the Type<br/>(else dictionary lookup)
    S-->>R: Func<obj, 'T>
    R->>D: Invoke(closure)
    D->>D: read captured fields
    D->>C: site.Target(site, target, args…)
    C->>C: rule cache: restriction on runtime type
    C-->>D: obj (or a value for a typed site)
    D-->>U: 'T
```

About 20 ns for `w?Add(i, 1)` against 7.5 for C# `dynamic`: what remains is the block's entry
(the closure, `GetType()`, the compare, the invoke); the site call itself is C#'s.

## The first call at a site

```mermaid
sequenceDiagram
    participant S as Sites<'T>
    participant DC as DlrCache
    participant Di as Discover
    participant T as Translate
    participant B as Binders
    participant L as LeafExpressionConverter + SiteHoister

    S->>DC: getOrCompile(closureType, file, line)
    DC->>Di: findBody(builderType, closureType, file, line)
    Di->>Di: reflected definitions of the closure's declaring type<br/>(decoded once per type, assembly-wide fallback)
    Di-->>DC: Found (Context, MemberBody, Body)
    DC->>T: translate
    T->>T: normalize (pipes, curried markers, literal lets)
    loop each marker
        T->>B: getMember / invokeMember / … (context, name, args)
        B-->>T: quotation Call on a baked CallSite
    end
    T->>L: NewDelegate over the closure → LINQ → hoist sites → Compile()
    L-->>DC: Func<obj, 'T>
    DC-->>S: Compiled (cached by closure type, typed entry installed)
```

Microseconds to low milliseconds, once per site: decoding the reflected definition (per type),
building the binders and sites, `Compile()`. The DLR's own first bind per site (the rule) is
paid on the first invoke like any C# `dynamic` call site.

## Measured

Release, net10.0, Apple Silicon; the current numbers for every path are in
[benchmarks.md](benchmarks.md) (`Benchmarks/bench.sh docs`). Older spot measurements, for the
function-member paths:

| | ns |
| --- | --- |
| block, `w?Add(i, 1)` on a method | 18 (was 29 before the hoisted sites and typed cache) |
| block, `e?Fn(i)` with `Fn` an F# function property | 33 |
| one site alternating between the two kinds | 70 |
| bound `int -> int -> int`, full application | 11 |
| bound `unit -> int` property read | 8.5 |
| static `w.Add(i, 1)` | 11–15 |
