# Benchmarks

BenchmarkDotNet over the library's hot paths, next to what they replace.

```
./bench.sh              # every suite (minutes)
./bench.sh Core         # one suite: Core (the operations) or Targets (JObject, Expando)
./bench.sh Core short   # BenchmarkDotNet's short job, for a quick look
```

Results: `BenchmarkDotNet.Artifacts/results/*-report-github.md`, ready to paste into the README's
"Measured" table. Numbers are steady state: every block is bound and compiled during warm-up, so a
row is the per-call cost of the compiled block, its sites' cached rules and the operation itself.
`MemoryDiagnoser` shows the per-call allocation (the block's closure, plus a box for a value
result — see `Tests/HotPath.fs`).

Suites: `Core` — baselines (static, cached reflection, FSharp.Interop.Dynamic) against `dlr`
get/call/set, a loop, a computed name, an F# function member, an omitted optional parameter, a
lambda for a `Func` parameter, static overloads, a constructor, structural `?=?`. `Targets` —
`JObject` and `ExpandoObject` reads against `JObject`'s own typed access.
