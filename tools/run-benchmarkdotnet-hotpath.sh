#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

DOTNET_CLI="${DOTNET_CLI:-$(command -v dotnet || true)}"
if [[ -z "$DOTNET_CLI" && -x /usr/local/share/dotnet/dotnet ]]; then
  DOTNET_CLI="/usr/local/share/dotnet/dotnet"
fi
if [[ -z "$DOTNET_CLI" ]]; then
  echo "dotnet CLI not found. Set DOTNET_CLI=/path/to/dotnet." >&2
  exit 127
fi
export PATH="$(dirname "$DOTNET_CLI"):$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(dirname "$DOTNET_CLI")")}"

ARTIFACT_DIR="${ARTIFACT_DIR:-TestResults/accesscity-benchmarkdotnet}"
FILTER="${FILTER:-*RiskHotPathBenchmarks*}"
JOB_ARGS="${JOB_ARGS:-}"

mkdir -p "$ARTIFACT_DIR"

"$DOTNET_CLI" run \
  --project AccessCity.Benchmarks/AccessCity.Benchmarks.csproj \
  --configuration Release \
  -- \
  --filter "$FILTER" \
  --artifacts "$ARTIFACT_DIR" \
  --cli "$DOTNET_CLI" \
  $JOB_ARGS

echo "BenchmarkDotNet artifacts: $ARTIFACT_DIR"
