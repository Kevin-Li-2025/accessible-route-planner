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

CXX="${CXX:-c++}"
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

node - "$ARTIFACT_DIR" <<'NODE'
const fs = require('fs');
const dir = process.argv[2];
function parsePerf(path, operations) {
  const rows = fs.readFileSync(path, 'utf8')
    .split(/\n/)
    .map((line) => line.split(','))
    .filter((cols) => cols.length >= 3 && cols[0] && !cols[0].startsWith('#'));
  const out = { operations };
  for (const cols of rows) {
    const value = Number(cols[0]);
    const event = cols[2];
    if (Number.isFinite(value)) out[event] = value;
  }
  if (out.cycles) out.cyclesPerOperation = out.cycles / operations;
  if (out.instructions && out.cycles) out.ipc = out.instructions / out.cycles;
  if (out['cache-misses'] && out['cache-references']) out.cacheMissRate = out['cache-misses'] / out['cache-references'];
  if (out['branch-misses'] && out.branches) out.branchMissRate = out['branch-misses'] / out.branches;
  return out;
}
const summary = {
  generatedAtUtc: new Date().toISOString(),
  riskKernel: parsePerf(`${dir}/risk_kernel_perf_stat.csv`, JSON.parse(fs.readFileSync(`${dir}/risk_kernel_report.json`, 'utf8')).queries),
  marketDataReplay: parsePerf(`${dir}/market_data_replay_perf_stat.csv`, JSON.parse(fs.readFileSync(`${dir}/market_data_replay_report.json`, 'utf8')).received)
};
fs.writeFileSync(`${dir}/linux_perf_summary.json`, JSON.stringify(summary, null, 2));
NODE

echo "Linux perf artifacts: $ARTIFACT_DIR"
