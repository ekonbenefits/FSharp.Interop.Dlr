# Benchmarks

BenchmarkDotNet over the library's hot paths, next to what they replace.

```
./bench.sh              # every suite (minutes)
./bench.sh Core         # one suite: Core (the operations) or Targets (JObject, Expando)
./bench.sh Core short   # BenchmarkDotNet's short job, for a quick look
./bench.sh docs         # run everything and write docs/benchmarks.md + the README's "Measured" table
```

`docs` (add `short` for a quick pass) writes two things: `docs/benchmarks.md` — comparison tables,
the same operation done each way (static, cached reflection, FSharp.Interop.Dynamic, C# `dynamic`,
`dlr { }`), laid out in `Program.fs`'s `writeDocs` — and the README's short table between its
`<!-- benchmarks:start/end -->` markers, from `readmeRows`. Raw BenchmarkDotNet output is in
`BenchmarkDotNet.Artifacts/results/`. Numbers are steady state: every block is bound and compiled during warm-up, so a
row is the per-call cost of the compiled block, its sites' cached rules and the operation itself.
`MemoryDiagnoser` shows the per-call allocation (the block's closure, plus a box for a value
result — see `Tests/HotPath.fs`).

`Benchmarks/CSharp` holds the C# `dynamic` equivalents (`dynamic d = o; d.Count`, …) as static
methods the F# suites call, so they appear in the same tables: the same binders and site rules,
minus the block's fixed cost (closure, cache lookup, delegate invoke), which is the gap to close.

Suites: `Core` — baselines (static, cached reflection, FSharp.Interop.Dynamic, C# `dynamic`) against `dlr`
get/call/set, a loop, a computed name, an F# function member, an omitted optional parameter, a
lambda for a `Func` parameter, static overloads, a constructor, structural `?=?`. `Targets` —
`JObject` and `ExpandoObject` reads against `JObject`'s own typed access.
