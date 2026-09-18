#!/usr/bin/env bash
# Runs the benchmarks.
#   ./bench.sh              every suite            ./bench.sh Core          one suite
#   ./bench.sh Core short   quick look             ./bench.sh docs [short]  write docs/benchmarks.md and the README table
# Raw results land in Benchmarks/BenchmarkDotNet.Artifacts/results.
set -euo pipefail
cd "$(dirname "$0")"
if [[ "${1:-}" == "docs" ]]; then
  dotnet run -c Release --no-launch-profile -- docs "${2:-}"
  exit
fi
filter="${1:-*}"
job="${2:-default}"
args=(--filter "*${filter}*")
[[ "$job" == "short" ]] && args+=(--job short)
dotnet run -c Release --no-launch-profile -- "${args[@]}"
