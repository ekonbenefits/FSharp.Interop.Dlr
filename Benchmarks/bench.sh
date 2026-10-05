#!/usr/bin/env bash
# Runs the benchmarks.
#   ./bench.sh              every suite            ./bench.sh Core          one suite
#   ./bench.sh Core short   quick look             ./bench.sh docs [short]  write docs/benchmarks.md and the README table
#   ./bench.sh compare [ref]  after `docs`: flag regressions in the dlr column against HEAD (or ref); exit 1 on any
# Raw results land in Benchmarks/BenchmarkDotNet.Artifacts/results.
set -euo pipefail
cd "$(dirname "$0")"
if [[ "${1:-}" == "compare" ]]; then
  dotnet fsi compare.fsx ${2:+"$2"}
  exit
fi
if [[ "${1:-}" == "docs" ]]; then
  dotnet run -c Release --no-launch-profile -- docs "${2:-}"
  exit
fi
filter="${1:-*}"
job="${2:-default}"
args=()
[[ "$job" == "short" ]] && args+=(short)   # the program picks the job (its config names the project)
args+=(--filter "*${filter}*")
dotnet run -c Release --no-launch-profile -- "${args[@]}"
