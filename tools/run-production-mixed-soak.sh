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
if ! command -v k6 >/dev/null 2>&1; then
  echo "k6 is required." >&2
  exit 127
fi

BASE_URL="${BASE_URL:-http://127.0.0.1:5099}"
PORT="${PORT:-5099}"
CITY_NAME="${CITY_NAME:-west-midlands}"
OSM_URL="${OSM_URL:-https://download.geofabrik.de/europe/united-kingdom/england/west-midlands-latest.osm.pbf}"
OSM_FILE="${OSM_FILE:-TestResults/accesscity-real-city-api-p99/${CITY_NAME}.osm.pbf}"
ARTIFACT_DIR="${ARTIFACT_DIR:-TestResults/accesscity-production-soak}"
ROUTE_COUNT="${ROUTE_COUNT:-1000}"
ROUTE_DATASET_FILE="${ROUTE_DATASET_FILE:-$ARTIFACT_DIR/birmingham-${ROUTE_COUNT}-routes.json}"
DURATION="${DURATION:-30m}"
SKIP_IMPORT="${SKIP_IMPORT:-true}"
WARMUP_ROUTE_CACHE="${WARMUP_ROUTE_CACHE:-false}"
EXTERNAL_OSRM_ENABLED="${EXTERNAL_OSRM_ENABLED:-false}"
FAILURE_INJECTION="${FAILURE_INJECTION:-none}"
FAILURE_AT_SECONDS="${FAILURE_AT_SECONDS:-300}"
RESOURCE_INTERVAL_SECONDS="${RESOURCE_INTERVAL_SECONDS:-5}"
SLO_OVERALL_P95_MS="${SLO_OVERALL_P95_MS:-1200}"
SLO_OVERALL_P99_MS="${SLO_OVERALL_P99_MS:-3000}"
SLO_ROUTE_P95_MS="${SLO_ROUTE_P95_MS:-500}"
SLO_ROUTE_P99_MS="${SLO_ROUTE_P99_MS:-2000}"
SLO_HOT_READ_P95_MS="${SLO_HOT_READ_P95_MS:-350}"
SLO_HOT_READ_P99_MS="${SLO_HOT_READ_P99_MS:-1200}"
SLO_RISK_P95_MS="${SLO_RISK_P95_MS:-250}"
SLO_RISK_P99_MS="${SLO_RISK_P99_MS:-1000}"
SLO_DASHBOARD_P95_MS="${SLO_DASHBOARD_P95_MS:-500}"
SLO_DASHBOARD_P99_MS="${SLO_DASHBOARD_P99_MS:-1500}"
SLO_CHECK_RATE="${SLO_CHECK_RATE:-0.97}"
SLO_HTTP_FAILURE_RATE="${SLO_HTTP_FAILURE_RATE:-0.02}"
SLO_PRODUCTION_FAILURE_RATE="${SLO_PRODUCTION_FAILURE_RATE:-0.02}"
SLO_ROUTE_TIMEOUT_RATE="${SLO_ROUTE_TIMEOUT_RATE:-0.02}"

JWT_KEY="${JWT_KEY:-AccessCity_Local_Production_Soak_Jwt_Key_Placeholder_Not_For_Production_64_Bytes}"
JWT_ISSUER="${JWT_ISSUER:-AccessCity.Local}"
JWT_AUDIENCE="${JWT_AUDIENCE:-AccessCity.Local}"
CONNECTION_STRING="${ConnectionStrings__DefaultConnection:-Host=localhost;Port=5432;Database=accesscitydb;Username=accesscity;Password=accesscity123}"

mkdir -p "$ARTIFACT_DIR" "$(dirname "$OSM_FILE")"
API_PID_FILE="$ARTIFACT_DIR/api.pid"
PIDS_TO_CLEAN_FILE="$ARTIFACT_DIR/pids-to-clean.txt"
: > "$PIDS_TO_CLEAN_FILE"

if [[ ! -f "$ROUTE_DATASET_FILE" ]]; then
  ROUTE_COUNT="$ROUTE_COUNT" OUTPUT="$ROUTE_DATASET_FILE" node tools/generate-birmingham-route-pairs.js >/dev/null
fi
ROUTE_DATASET_FILE="$(cd "$(dirname "$ROUTE_DATASET_FILE")" && pwd)/$(basename "$ROUTE_DATASET_FILE")"

if [[ ! -f "$OSM_FILE" ]]; then
  echo "Downloading $CITY_NAME OSM extract from $OSM_URL"
  curl -L --fail --retry 3 --output "$OSM_FILE.tmp" "$OSM_URL"
  mv "$OSM_FILE.tmp" "$OSM_FILE"
fi
if file "$OSM_FILE" | grep -Eqi 'html|text'; then
  echo "Downloaded OSM file does not look like a PBF extract: $OSM_FILE" >&2
  exit 2
fi

"$DOTNET_CLI" build CodeConquerors.sln --configuration Release --nologo >/dev/null

start_api() {
  ASPNETCORE_ENVIRONMENT=Development \
  ASPNETCORE_URLS="http://127.0.0.1:${PORT}" \
  Jwt__Key="$JWT_KEY" \
  Jwt__Issuer="$JWT_ISSUER" \
  Jwt__Audience="$JWT_AUDIENCE" \
  ConnectionStrings__DefaultConnection="$CONNECTION_STRING" \
  DATABASE_URL='' \
  Postgres__AutoMigrate=false \
  Postgres__AutoSchemaMaintenance=false \
  Messaging__UseKafka=false \
  Workers__OsmImport__Enabled=false \
  Workers__Routing__Enabled=false \
  Workers__TileWarming__Enabled=false \
  Routing__RouteGraphWarmupEnabled=false \
  Routing__ExternalOsrmEnabled="$EXTERNAL_OSRM_ENABLED" \
  HotPathWarmup__Enabled=false \
  ExternalApis__Overpass__RealtimeHazardsEnabled=false \
  OsmImport__FilePath="$OSM_FILE" \
  OsmImport__ReplaceExisting=true \
  RateLimiting__RoutingHeavy__PermitLimit=1000000 \
  RateLimiting__Global__PermitLimit=2000000 \
  "$DOTNET_CLI" AccessCity.API/bin/Release/net9.0/AccessCity.API.dll \
    >>"$ARTIFACT_DIR/api.log" 2>&1 &
  API_PID=$!
  echo "$API_PID" > "$API_PID_FILE"
  echo "$API_PID" >> "$PIDS_TO_CLEAN_FILE"
}

wait_ready() {
  for _ in $(seq 1 90); do
    if curl -fsS "${BASE_URL}/health" >/dev/null; then
      return 0
    fi
    sleep 1
  done
  tail -120 "$ARTIFACT_DIR/api.log" >&2 || true
  return 1
}

cleanup() {
  for pid in "${RESOURCE_PID:-}" "${FAILURE_PID:-}" "${API_PID:-}"; do
    if [[ -n "$pid" ]] && kill -0 "$pid" >/dev/null 2>&1; then
      kill "$pid" >/dev/null 2>&1 || true
      wait "$pid" >/dev/null 2>&1 || true
    fi
  done
  if [[ -f "$PIDS_TO_CLEAN_FILE" ]]; then
    while read -r pid; do
      if [[ -n "$pid" ]] && kill -0 "$pid" >/dev/null 2>&1; then
        kill "$pid" >/dev/null 2>&1 || true
      fi
    done < "$PIDS_TO_CLEAN_FILE"
  fi
}
trap cleanup EXIT

: > "$ARTIFACT_DIR/api.log"
start_api
wait_ready

email="soak-admin-$(date +%s)@accesscity.local"
password="AccessCitySoak!$(date +%s)"
token="$(curl -fsS -X POST "${BASE_URL}/api/v1/Auth/register" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$email\",\"password\":\"$password\",\"fullName\":\"Production Soak Runner\"}" \
  | node -e "let s='';process.stdin.on('data',d=>s+=d);process.stdin.on('end',()=>process.stdout.write(JSON.parse(s).token||''));")"

if [[ "$SKIP_IMPORT" == "true" ]]; then
  echo "{\"skipped\":true,\"reason\":\"SKIP_IMPORT=true\"}" > "$ARTIFACT_DIR/osm-import-response.json"
else
  curl -fsS -X POST "${BASE_URL}/api/v1/admin/osm/import" \
    -H "Authorization: Bearer $token" \
    -o "$ARTIFACT_DIR/osm-import-response.json"
fi

curl -fsS "${BASE_URL}/api/v1/routing/route-graph/status" \
  -o "$ARTIFACT_DIR/route-graph-status.json"

if [[ "$WARMUP_ROUTE_CACHE" == "true" ]]; then
  BASE_URL="$BASE_URL" ROUTE_DATASET_FILE="$ROUTE_DATASET_FILE" DURATION=15s ROUTE_RATE=20 \
    ROUTE_P95_MS=5000 ROUTE_P99_MS=10000 ARTIFACT_DIR="$ARTIFACT_DIR/warmup" \
    tools/run-routing-api-p99-test.sh >/dev/null
fi

INTERVAL_SECONDS="$RESOURCE_INTERVAL_SECONDS" \
  tools/sample-process-resources.sh "$API_PID" "$ARTIFACT_DIR/resource-timeseries.csv" &
RESOURCE_PID=$!

if [[ "$FAILURE_INJECTION" == "api-restart" ]]; then
  (
    sleep "$FAILURE_AT_SECONDS"
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ),api-restart,start" >> "$ARTIFACT_DIR/failure-injection.log"
    old_api_pid="$(cat "$API_PID_FILE" 2>/dev/null || true)"
    if [[ -n "$old_api_pid" ]]; then
      kill "$old_api_pid" >/dev/null 2>&1 || true
      wait "$old_api_pid" >/dev/null 2>&1 || true
    fi
    start_api
    INTERVAL_SECONDS="$RESOURCE_INTERVAL_SECONDS" \
      tools/sample-process-resources.sh "$API_PID" "$ARTIFACT_DIR/resource-timeseries-after-restart.csv" &
    echo "$!" >> "$PIDS_TO_CLEAN_FILE"
    wait_ready
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ),api-restart,ready" >> "$ARTIFACT_DIR/failure-injection.log"
  ) &
  FAILURE_PID=$!
fi

set +e
BASE_URL="$BASE_URL" \
ROUTE_DATASET_FILE="$ROUTE_DATASET_FILE" \
DURATION="$DURATION" \
SLO_OVERALL_P95_MS="$SLO_OVERALL_P95_MS" \
SLO_OVERALL_P99_MS="$SLO_OVERALL_P99_MS" \
SLO_ROUTE_P95_MS="$SLO_ROUTE_P95_MS" \
SLO_ROUTE_P99_MS="$SLO_ROUTE_P99_MS" \
SLO_HOT_READ_P95_MS="$SLO_HOT_READ_P95_MS" \
SLO_HOT_READ_P99_MS="$SLO_HOT_READ_P99_MS" \
SLO_RISK_P95_MS="$SLO_RISK_P95_MS" \
SLO_RISK_P99_MS="$SLO_RISK_P99_MS" \
SLO_DASHBOARD_P95_MS="$SLO_DASHBOARD_P95_MS" \
SLO_DASHBOARD_P99_MS="$SLO_DASHBOARD_P99_MS" \
SLO_CHECK_RATE="$SLO_CHECK_RATE" \
SLO_HTTP_FAILURE_RATE="$SLO_HTTP_FAILURE_RATE" \
SLO_PRODUCTION_FAILURE_RATE="$SLO_PRODUCTION_FAILURE_RATE" \
SLO_ROUTE_TIMEOUT_RATE="$SLO_ROUTE_TIMEOUT_RATE" \
ARTIFACT_DIR="$ARTIFACT_DIR/k6" \
k6 run --summary-export "$ARTIFACT_DIR/k6-production-mixed-summary.json" \
  tools/k6/accesscity-production-mixed-soak.js
K6_EXIT_CODE=$?
set -e

node - "$ARTIFACT_DIR" "$ROUTE_DATASET_FILE" "$K6_EXIT_CODE" <<'NODE'
const fs = require('fs');
const os = require('os');
const [dir, routeDatasetFile, k6ExitCodeText] = process.argv.slice(2);
const summary = JSON.parse(fs.readFileSync(`${dir}/k6-production-mixed-summary.json`, 'utf8'));
const graph = JSON.parse(fs.readFileSync(`${dir}/route-graph-status.json`, 'utf8'));
const routes = JSON.parse(fs.readFileSync(routeDatasetFile, 'utf8'));
const metric = (name) => summary.metrics[name] || {};
const k6ExitCode = Number(k6ExitCodeText || 0);
const report = {
  harnessVersion: 'accesscity-production-mixed-soak-v1',
  generatedAtUtc: new Date().toISOString(),
  k6ExitCode,
  k6Passed: k6ExitCode === 0,
  routePairCount: routes.routes.length,
  routeGraph: graph,
  machine: { platform: process.platform, arch: process.arch, cpus: os.cpus().length, totalMemoryBytes: os.totalmem() },
  httpReqs: metric('http_reqs'),
  httpReqDurationMs: metric('http_req_duration'),
  httpReqFailed: metric('http_req_failed'),
  productionApiFailure: metric('production_api_failure'),
  routeJobTimeout: metric('route_job_timeout'),
  checks: metric('checks'),
  resourceTimeseries: `${dir}/resource-timeseries.csv`,
  claimBoundary: 'Local production-mixed soak against one AccessCity.API process and local dependencies. Use Kubernetes capacity validation for distributed 1M req/s target claims.'
};
fs.writeFileSync(`${dir}/production_mixed_soak_report.json`, JSON.stringify(report, null, 2));
console.log(`Report written: ${dir}/production_mixed_soak_report.json`);
NODE

exit "$K6_EXIT_CODE"
