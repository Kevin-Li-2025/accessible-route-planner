#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

CXX="${CXX:-c++}"
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

node - "$ARTIFACT_DIR" <<'NODE'
const fs = require('fs');
const dir = process.argv[2];
const files = [
  'spsc_replay_report.json',
  'udp_loopback_report.json',
  'tcp_loopback_report.json'
];
const rows = files.map((file) => JSON.parse(fs.readFileSync(`${dir}/${file}`, 'utf8')));
fs.writeFileSync(`${dir}/market_data_summary.json`, JSON.stringify(rows, null, 2));
const lines = [
  '# AccessCity Market Data Benchmark',
  '',
  '| Mode | Messages | Received | Losses | Throughput msg/s | p50 ns | p95 ns | p99 ns | max ns |',
  '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |'
];
for (const row of rows) {
  lines.push(`| ${row.mode} | ${row.messages} | ${row.received} | ${row.losses} | ${Number(row.throughputMessagesPerSecond).toFixed(2)} | ${Number(row.latencyNanoseconds.p50).toFixed(2)} | ${Number(row.latencyNanoseconds.p95).toFixed(2)} | ${Number(row.latencyNanoseconds.p99).toFixed(2)} | ${Number(row.latencyNanoseconds.max).toFixed(2)} |`);
}
fs.writeFileSync(`${dir}/market_data_summary.md`, `${lines.join('\n')}\n`);
NODE

echo "Market data benchmark artifacts: $ARTIFACT_DIR"
