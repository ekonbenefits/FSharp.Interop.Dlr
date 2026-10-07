#!/usr/bin/env bash
# Differential test of captures (#200): generate the cases, run them in Debug and in Release,
# check Release against Debug, and check the analyzer's DLR007 warns of exactly the cases Release
# refuses. Exit 1 on a case Release binds silently wrong, or on any disagreement.
set -euo pipefail
cd "$(dirname "$0")"
dotnet fsi generate.fsx
out=obj/differential
mkdir -p "$out"
for c in debug release; do
  dotnet build -c $c > /dev/null
  dotnet run -c $c --no-build > "$out/$c.txt"
done
# The analyzer per file (analyze.fsx), over the fsc arguments the Release build used.
dotnet build ../FSharp.Interop.Dlr.Analyzers -c Release > /dev/null
dotnet build -c Release --no-incremental -p:ProvideCommandLineArgs=true -getItem:FscCommandLineArgs > "$out/fscargs.json"
python3 -c 'import json,sys; print("\n".join(i["Identity"] for i in json.load(open(sys.argv[1]))["Items"]["FscCommandLineArgs"] if not i["Identity"].startswith("--embed")))' "$out/fscargs.json" > "$out/fsc.args"
dotnet fsi analyze.fsx "$out/fsc.args" > "$out/analyzer.txt" < /dev/null
dotnet fsi compare.fsx "$out/debug.txt" "$out/release.txt" "$out/analyzer.txt"
