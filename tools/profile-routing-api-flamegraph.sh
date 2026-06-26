#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

PID="${1:-}"
DURATION_SECONDS="${DURATION_SECONDS:-60}"
ARTIFACT_DIR="${ARTIFACT_DIR:-TestResults/accesscity-flamegraph}"
TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
TRACE_FILE="$ARTIFACT_DIR/routing-api-$TIMESTAMP.nettrace"
SPEEDSCOPE_FILE="$ARTIFACT_DIR/routing-api-$TIMESTAMP.speedscope.json"

if [[ -z "$PID" ]]; then
  echo "Usage: $0 <AccessCity.API pid>" >&2
  echo "Find it with: pgrep -fl AccessCity.API" >&2
  exit 2
fi

DOTNET_CLI="${DOTNET_CLI:-$(command -v dotnet || true)}"
if [[ -z "$DOTNET_CLI" && -x /usr/local/share/dotnet/dotnet ]]; then
  DOTNET_CLI="/usr/local/share/dotnet/dotnet"
fi

DOTNET_TRACE=(dotnet-trace)
if ! command -v dotnet-trace >/dev/null 2>&1; then
  if [[ -z "$DOTNET_CLI" ]]; then
    echo "dotnet-trace is required and dotnet CLI was not found. Set DOTNET_CLI=/path/to/dotnet." >&2
    exit 127
  fi

  if ! "$DOTNET_CLI" tool list --local | grep -q '^dotnet-trace[[:space:]]'; then
    "$DOTNET_CLI" tool install --local dotnet-trace >/dev/null
  fi
  DOTNET_TRACE=("$DOTNET_CLI" tool run dotnet-trace --)
fi

mkdir -p "$ARTIFACT_DIR"

echo "Collecting CPU profile from pid=$PID for ${DURATION_SECONDS}s"
"${DOTNET_TRACE[@]}" collect \
  --process-id "$PID" \
  --profile cpu-sampling \
  --duration "00:00:${DURATION_SECONDS}" \
  --output "$TRACE_FILE" \
  --format speedscope

if [[ -f "${TRACE_FILE%.nettrace}.speedscope.json" ]]; then
  mv "${TRACE_FILE%.nettrace}.speedscope.json" "$SPEEDSCOPE_FILE"
fi

echo "Trace: $TRACE_FILE"
echo "Speedscope: $SPEEDSCOPE_FILE"
