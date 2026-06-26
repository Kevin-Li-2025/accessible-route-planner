#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

BASE_URL="${BASE_URL:-http://127.0.0.1:5099}"
DURATION="${DURATION:-3m}"
ROUTE_RATE="${ROUTE_RATE:-8}"
PREALLOCATED_VUS="${PREALLOCATED_VUS:-80}"
MAX_VUS="${MAX_VUS:-400}"
ROUTE_P95_MS="${ROUTE_P95_MS:-1000}"
ROUTE_P99_MS="${ROUTE_P99_MS:-2500}"
ROUTE_DATASET="${ROUTE_DATASET:-birmingham}"
ROUTE_DATASET_FILE="${ROUTE_DATASET_FILE:-}"
ARTIFACT_DIR="${ARTIFACT_DIR:-TestResults/accesscity-routing-api-p99}"
SUMMARY_JSON="$ARTIFACT_DIR/k6-routing-api-summary.json"

mkdir -p "$ARTIFACT_DIR"

if ! command -v k6 >/dev/null 2>&1; then
  echo "k6 is required. Install it first: https://grafana.com/docs/k6/latest/set-up/install-k6/" >&2
  exit 127
fi

echo "Running routing API p99 test against $BASE_URL"
BASE_URL="$BASE_URL" \
DURATION="$DURATION" \
ROUTE_RATE="$ROUTE_RATE" \
PREALLOCATED_VUS="$PREALLOCATED_VUS" \
MAX_VUS="$MAX_VUS" \
ROUTE_P95_MS="$ROUTE_P95_MS" \
ROUTE_P99_MS="$ROUTE_P99_MS" \
ROUTE_DATASET="$ROUTE_DATASET" \
ROUTE_DATASET_FILE="$ROUTE_DATASET_FILE" \
k6 run \
  --summary-export "$SUMMARY_JSON" \
  tools/k6/accesscity-routing-api-p99.js

echo "Summary written to $SUMMARY_JSON"
