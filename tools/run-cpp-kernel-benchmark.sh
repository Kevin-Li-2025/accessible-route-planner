#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

CXX="${CXX:-c++}"
QUERIES="${QUERIES:-1000000}"
BATCH_SIZE="${BATCH_SIZE:-256}"
GRID_CELLS="${GRID_CELLS:-262144}"
ARTIFACT_DIR="${ARTIFACT_DIR:-TestResults/accesscity-cpp-kernel}"
BIN="$ARTIFACT_DIR/risk_kernel_benchmark"
REPORT="$ARTIFACT_DIR/cpp_kernel_report.json"

mkdir -p "$ARTIFACT_DIR"

"$CXX" -O3 -std=c++20 -march=native \
  tools/cpp/risk_kernel_benchmark.cpp \
  -o "$BIN"

"$BIN" "$QUERIES" "$BATCH_SIZE" "$GRID_CELLS" | tee "$REPORT"

echo "C++ kernel benchmark report: $REPORT"
