#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "Linux perf counters require Linux. Current platform: $(uname -s)" >&2
  exit 2
fi
if ! command -v perf >/dev/null 2>&1; then
  echo "perf is required. Install linux-tools/perf for this kernel." >&2
  exit 127
fi
if [[ -r /proc/sys/kernel/perf_event_paranoid ]]; then
  paranoid="$(cat /proc/sys/kernel/perf_event_paranoid)"
  if [[ "$paranoid" =~ ^[0-9]+$ && "$paranoid" -ge 2 ]]; then
    echo "perf_event_paranoid=$paranoid blocks unprivileged CPU profiling on this host." >&2
    echo "Temporarily run: sudo sysctl kernel.perf_event_paranoid=-1" >&2
    echo "Or grant CAP_PERFMON/CAP_SYS_PTRACE/CAP_SYS_ADMIN for perf." >&2
    exit 13
  fi
fi

CXX="${CXX:-c++}"
PYTHON="${PYTHON:-}"
if [[ -z "$PYTHON" ]]; then
  if [[ -x /usr/bin/python3 ]]; then
    PYTHON="/usr/bin/python3"
  else
    PYTHON="$(command -v python3)"
  fi
fi
ARTIFACT_DIR="${ARTIFACT_DIR:-TestResults/accesscity-linux-perf}"
mkdir -p "$ARTIFACT_DIR"

RISK_BIN="$ARTIFACT_DIR/risk_kernel_benchmark"
MARKET_BIN="$ARTIFACT_DIR/market_data_benchmark"

"$CXX" -O3 -std=c++20 -march=native -pthread tools/cpp/risk_kernel_benchmark.cpp -o "$RISK_BIN"
"$CXX" -O3 -std=c++20 -march=native -pthread tools/cpp/market_data_benchmark.cpp -o "$MARKET_BIN"

PERF_EVENTS="${PERF_EVENTS:-cycles,instructions,cache-references,cache-misses,branches,branch-misses,context-switches,cpu-migrations}"
QUERIES="${QUERIES:-10000000}"
HAZARDS="${HAZARDS:-1000000}"
THREADS="${THREADS:-$(nproc)}"
MESSAGES="${MESSAGES:-1000000}"
PRODUCER_CPU="${PRODUCER_CPU:-0}"
CONSUMER_CPU="${CONSUMER_CPU:-1}"

perf stat -x, -e "$PERF_EVENTS" \
  -o "$ARTIFACT_DIR/risk_kernel_perf_stat.csv" \
  "$RISK_BIN" "$QUERIES" 512 262144 "$HAZARDS" "$THREADS" \
  > "$ARTIFACT_DIR/risk_kernel_report.json"

perf record -F 999 -g \
  -o "$ARTIFACT_DIR/risk_kernel_perf.data" \
  "$RISK_BIN" "$QUERIES" 512 262144 "$HAZARDS" "$THREADS" \
  > "$ARTIFACT_DIR/risk_kernel_perf_record_report.json"
perf report --stdio -i "$ARTIFACT_DIR/risk_kernel_perf.data" \
  > "$ARTIFACT_DIR/risk_kernel_perf_report.txt"

perf stat -x, -e "$PERF_EVENTS" \
  -o "$ARTIFACT_DIR/market_data_replay_perf_stat.csv" \
  "$MARKET_BIN" replay "$MESSAGES" 39091 "$PRODUCER_CPU" "$CONSUMER_CPU" \
  > "$ARTIFACT_DIR/market_data_replay_report.json"

perf record -F 999 -g \
  -o "$ARTIFACT_DIR/market_data_replay_perf.data" \
  "$MARKET_BIN" replay "$MESSAGES" 39091 "$PRODUCER_CPU" "$CONSUMER_CPU" \
  > "$ARTIFACT_DIR/market_data_replay_perf_record_report.json"
perf report --stdio -i "$ARTIFACT_DIR/market_data_replay_perf.data" \
  > "$ARTIFACT_DIR/market_data_replay_perf_report.txt"

"$PYTHON" - "$ARTIFACT_DIR" <<'PY'
import csv
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

artifact_dir = Path(sys.argv[1])

def parse_perf(path: Path, operations: int) -> dict:
    out = {"operations": operations}
    with path.open(newline="") as handle:
        for row in csv.reader(handle):
            if len(row) < 3 or not row[0] or row[0].startswith("#"):
                continue
            try:
                value = float(row[0])
            except ValueError:
                continue
            out[row[2]] = value
    if out.get("cycles"):
        out["cyclesPerOperation"] = out["cycles"] / operations
    if out.get("instructions") and out.get("cycles"):
        out["ipc"] = out["instructions"] / out["cycles"]
    if out.get("cache-misses") and out.get("cache-references"):
        out["cacheMissRate"] = out["cache-misses"] / out["cache-references"]
    if out.get("branch-misses") and out.get("branches"):
        out["branchMissRate"] = out["branch-misses"] / out["branches"]
    return out

risk = json.loads((artifact_dir / "risk_kernel_report.json").read_text())
market = json.loads((artifact_dir / "market_data_replay_report.json").read_text())
summary = {
    "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
    "riskKernel": parse_perf(artifact_dir / "risk_kernel_perf_stat.csv", risk["queries"]),
    "marketDataReplay": parse_perf(artifact_dir / "market_data_replay_perf_stat.csv", market["received"]),
}
(artifact_dir / "linux_perf_summary.json").write_text(json.dumps(summary, indent=2) + "\n")
PY

echo "Linux perf artifacts: $ARTIFACT_DIR"
