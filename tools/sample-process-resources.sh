#!/usr/bin/env bash
set -euo pipefail

PID="${1:-}"
OUTPUT="${2:-TestResults/accesscity-production-soak/resource-timeseries.csv}"
INTERVAL_SECONDS="${INTERVAL_SECONDS:-5}"

if [[ -z "$PID" ]]; then
  echo "Usage: $0 <pid> [output.csv]" >&2
  exit 2
fi

mkdir -p "$(dirname "$OUTPUT")"
echo "timestampUtc,pid,cpuPercent,rssBytes,vszBytes,threads" > "$OUTPUT"

while kill -0 "$PID" >/dev/null 2>&1; do
  timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  sample="$(ps -p "$PID" -o %cpu=,rss=,vsz=,nlwp= 2>/dev/null || true)"
  if [[ -z "$sample" ]]; then
    sample="$(ps -p "$PID" -o %cpu=,rss=,vsz= 2>/dev/null || true)"
  fi
  awk -v ts="$timestamp" -v pid="$PID" '
    NF >= 3 {
      threads = NF >= 4 ? $4 : "";
      printf "%s,%s,%.2f,%d,%d,%s\n", ts, pid, $1, $2 * 1024, $3 * 1024, threads
    }' <<<"$sample" >> "$OUTPUT" || true
  sleep "$INTERVAL_SECONDS"
done
