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
dotnet build ../FSharp.Interop.Dlr.Analyzers -c Release > /dev/null
dotnet fsharp-analyzers --project Differential.fsproj --analyzers-path ../FSharp.Interop.Dlr.Analyzers/bin/Release/net10.0 --code-root .. > "$out/analyzer.txt" 2>&1 || true
dotnet fsi compare.fsx "$out/debug.txt" "$out/release.txt" "$out/analyzer.txt"
