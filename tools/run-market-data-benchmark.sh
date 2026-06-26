#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

CXX="${CXX:-c++}"
PYTHON="${PYTHON:-}"
if [[ -z "$PYTHON" ]]; then
  if [[ -x /usr/bin/python3 ]]; then
    PYTHON="/usr/bin/python3"
  else
    PYTHON="$(command -v python3)"
  fi
fi
MESSAGES="${MESSAGES:-1000000}"
NETWORK_MESSAGES="${NETWORK_MESSAGES:-200000}"
PORT="${PORT:-39091}"
PRODUCER_CPU="${PRODUCER_CPU:--1}"
CONSUMER_CPU="${CONSUMER_CPU:--1}"
ARTIFACT_DIR="${ARTIFACT_DIR:-TestResults/accesscity-market-data}"
BIN="$ARTIFACT_DIR/market_data_benchmark"

mkdir -p "$ARTIFACT_DIR"

"$CXX" -O3 -std=c++20 -march=native -pthread \
  tools/cpp/market_data_benchmark.cpp \
  -o "$BIN"

"$BIN" replay "$MESSAGES" "$PORT" "$PRODUCER_CPU" "$CONSUMER_CPU" \
  | tee "$ARTIFACT_DIR/spsc_replay_report.json"
"$BIN" udp "$NETWORK_MESSAGES" "$PORT" "$PRODUCER_CPU" "$CONSUMER_CPU" \
  | tee "$ARTIFACT_DIR/udp_loopback_report.json"
"$BIN" tcp "$NETWORK_MESSAGES" "$((PORT + 1))" "$PRODUCER_CPU" "$CONSUMER_CPU" \
  | tee "$ARTIFACT_DIR/tcp_loopback_report.json"

"$PYTHON" - "$ARTIFACT_DIR" <<'PY'
import json
import sys
from pathlib import Path

artifact_dir = Path(sys.argv[1])
files = [
    "spsc_replay_report.json",
    "udp_loopback_report.json",
    "tcp_loopback_report.json",
]
rows = [json.loads((artifact_dir / file).read_text()) for file in files]
(artifact_dir / "market_data_summary.json").write_text(json.dumps(rows, indent=2) + "\n")
lines = [
    "# AccessCity Market Data Benchmark",
    "",
    "| Mode | Messages | Received | Losses | Throughput msg/s | p50 ns | p95 ns | p99 ns | max ns |",
    "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
]
for row in rows:
    lat = row["latencyNanoseconds"]
    lines.append(
        f"| {row['mode']} | {row['messages']} | {row['received']} | {row['losses']} | "
        f"{row['throughputMessagesPerSecond']:.2f} | {lat['p50']:.2f} | {lat['p95']:.2f} | "
        f"{lat['p99']:.2f} | {lat['max']:.2f} |"
    )
(artifact_dir / "market_data_summary.md").write_text("\n".join(lines) + "\n")
PY

echo "Market data benchmark artifacts: $ARTIFACT_DIR"
