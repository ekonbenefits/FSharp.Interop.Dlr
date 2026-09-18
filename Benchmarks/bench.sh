#!/usr/bin/env bash
# Runs the benchmarks. Everything: ./bench.sh   One suite: ./bench.sh Core   Quick: ./bench.sh Core short
# Results land in Benchmarks/BenchmarkDotNet.Artifacts/results (markdown, ready for the README).
set -euo pipefail
cd "$(dirname "$0")"
filter="${1:-*}"
job="${2:-default}"
args=(--filter "*${filter}*")
[[ "$job" == "short" ]] && args+=(--job short)
dotnet run -c Release --no-launch-profile -- "${args[@]}"
