#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

CXX="${CXX:-c++}"
QUERIES="${QUERIES:-1000000}"
BATCH_SIZE="${BATCH_SIZE:-256}"
GRID_CELLS="${GRID_CELLS:-262144}"
HAZARDS="${HAZARDS:-1000000}"
THREADS="${THREADS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 1)}"
PERF_STAT="${PERF_STAT:-false}"
ARTIFACT_DIR="${ARTIFACT_DIR:-TestResults/accesscity-cpp-kernel}"
BIN="$ARTIFACT_DIR/risk_kernel_benchmark"
REPORT="$ARTIFACT_DIR/cpp_kernel_report.json"
PERF_REPORT="$ARTIFACT_DIR/cpp_kernel_perf_stat.txt"

mkdir -p "$ARTIFACT_DIR"

"$CXX" -O3 -std=c++20 -march=native \
  tools/cpp/risk_kernel_benchmark.cpp \
  -o "$BIN"

if [[ "$PERF_STAT" == "true" && "$(uname -s)" == "Linux" && -n "$(command -v perf || true)" ]]; then
  perf stat \
    -e cycles,instructions,cache-references,cache-misses,branches,branch-misses \
    -o "$PERF_REPORT" \
    "$BIN" "$QUERIES" "$BATCH_SIZE" "$GRID_CELLS" "$HAZARDS" "$THREADS" | tee "$REPORT"
  echo "perf stat report: $PERF_REPORT"
else
  "$BIN" "$QUERIES" "$BATCH_SIZE" "$GRID_CELLS" "$HAZARDS" "$THREADS" | tee "$REPORT"
fi

echo "C++ kernel benchmark report: $REPORT"
