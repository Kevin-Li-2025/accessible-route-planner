#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

RUNTIME="${RUNTIME:-osx-arm64}"
CONFIGURATION="${CONFIGURATION:-Release}"
OUT_DIR="${OUT_DIR:-TestResults/accesscity-native-aot-benchmark}"
QUERIES="${QUERIES:-1000000}"
BATCH_SIZE="${BATCH_SIZE:-256}"
GRID_CELLS="${GRID_CELLS:-262144}"
DOTNET_CLI="${DOTNET_CLI:-$(command -v dotnet || true)}"
if [[ -z "$DOTNET_CLI" && -x /usr/local/share/dotnet/dotnet ]]; then
  DOTNET_CLI="/usr/local/share/dotnet/dotnet"
fi
if [[ -z "$DOTNET_CLI" ]]; then
  echo "dotnet CLI not found. Set DOTNET_CLI=/path/to/dotnet." >&2
  exit 127
fi

mkdir -p "$OUT_DIR"

"$DOTNET_CLI" publish AccessCity.NativeAotKernels/AccessCity.NativeAotKernels.csproj \
  --configuration "$CONFIGURATION" \
  --runtime "$RUNTIME" \
  --self-contained true \
  -p:PublishAot=true \
  -o "$OUT_DIR/publish"

"$OUT_DIR/publish/AccessCity.NativeAotKernels" \
  --queries "$QUERIES" \
  --batch-size "$BATCH_SIZE" \
  --grid-cells "$GRID_CELLS" \
  | tee "$OUT_DIR/native_aot_kernel_report.json"

echo "NativeAOT benchmark artifacts: $OUT_DIR"
