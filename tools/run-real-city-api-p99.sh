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
if ! command -v curl >/dev/null 2>&1; then
  echo "curl is required." >&2
  exit 127
fi

BASE_URL="${BASE_URL:-http://127.0.0.1:5099}"
PORT="${PORT:-5099}"
CITY_NAME="${CITY_NAME:-monaco}"
OSM_URL="${OSM_URL:-https://download.geofabrik.de/europe/monaco-latest.osm.pbf}"
OSM_FILE="${OSM_FILE:-TestResults/accesscity-real-city-api-p99/${CITY_NAME}.osm.pbf}"
ROUTE_DATASET_FILE="${ROUTE_DATASET_FILE:-tools/k6/monaco-city-routes.json}"
DURATION="${DURATION:-60s}"
ROUTE_RATE="${ROUTE_RATE:-8}"
ROUTE_P95_MS="${ROUTE_P95_MS:-1500}"
ROUTE_P99_MS="${ROUTE_P99_MS:-3000}"
ARTIFACT_DIR="${ARTIFACT_DIR:-TestResults/accesscity-real-city-api-p99}"
PROFILE_FLAMEGRAPH="${PROFILE_FLAMEGRAPH:-false}"
PROFILE_DURATION_SECONDS="${PROFILE_DURATION_SECONDS:-${DURATION%s}}"
SKIP_IMPORT="${SKIP_IMPORT:-false}"
EXTERNAL_OSRM_ENABLED="${EXTERNAL_OSRM_ENABLED:-false}"
WARMUP_ROUTE_CACHE="${WARMUP_ROUTE_CACHE:-true}"
JWT_KEY="${JWT_KEY:-AccessCity_Local_P99_Test_Jwt_Key_Placeholder_Not_For_Production_64_Bytes}"
JWT_ISSUER="${JWT_ISSUER:-AccessCity.Local}"
JWT_AUDIENCE="${JWT_AUDIENCE:-AccessCity.Local}"
CONNECTION_STRING="${ConnectionStrings__DefaultConnection:-Host=localhost;Port=5432;Database=accesscitydb;Username=accesscity;Password=accesscity123}"

mkdir -p "$ARTIFACT_DIR" "$(dirname "$OSM_FILE")"
ROUTE_DATASET_FILE="$(cd "$(dirname "$ROUTE_DATASET_FILE")" && pwd)/$(basename "$ROUTE_DATASET_FILE")"

if [[ ! -f "$OSM_FILE" ]]; then
  echo "Downloading $CITY_NAME OSM extract from $OSM_URL"
  curl -L --fail --retry 3 --output "$OSM_FILE.tmp" "$OSM_URL"
  mv "$OSM_FILE.tmp" "$OSM_FILE"
fi
if file "$OSM_FILE" | grep -Eqi 'html|text'; then
  echo "Downloaded OSM file does not look like a PBF extract: $OSM_FILE" >&2
  echo "Check OSM_URL=$OSM_URL" >&2
  exit 2
fi

"$DOTNET_CLI" build CodeConquerors.sln --configuration Release --nologo >/dev/null

cleanup() {
  if [[ -n "${API_PID:-}" ]] && kill -0 "$API_PID" >/dev/null 2>&1; then
    kill "$API_PID" >/dev/null 2>&1 || true
    wait "$API_PID" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

echo "Starting AccessCity.API on port $PORT"
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
RateLimiting__RoutingHeavy__PermitLimit=10000 \
RateLimiting__Global__PermitLimit=100000 \
"$DOTNET_CLI" AccessCity.API/bin/Release/net9.0/AccessCity.API.dll \
  >"$ARTIFACT_DIR/api.log" 2>&1 &
API_PID=$!

api_ready=false
for _ in $(seq 1 60); do
  if curl -fsS "${BASE_URL}/health" >/dev/null; then
    api_ready=true
    break
  fi
  sleep 1
done
if [[ "$api_ready" != "true" ]]; then
  echo "AccessCity.API did not become healthy. Last logs:" >&2
  tail -80 "$ARTIFACT_DIR/api.log" >&2 || true
  exit 1
fi

email="city-p99-$(date +%s)@accesscity.local"
password="AccessCityP99!$(date +%s)"
register_payload="{\"email\":\"$email\",\"password\":\"$password\",\"fullName\":\"City P99 Runner\"}"
token="$(curl -fsS -X POST "${BASE_URL}/api/v1/Auth/register" -H 'Content-Type: application/json' -d "$register_payload" \
  | node -e "let s='';process.stdin.on('data',d=>s+=d);process.stdin.on('end',()=>process.stdout.write(JSON.parse(s).token||''));")"
if [[ -z "$token" ]]; then
  echo "Failed to register local p99 user." >&2
  exit 1
fi

echo "Importing OSM extract: $OSM_FILE"
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
  echo "Warming route cache with $ROUTE_DATASET_FILE"
  node - "$BASE_URL" "$ROUTE_DATASET_FILE" "$ARTIFACT_DIR/route-cache-warmup.json" <<'NODE'
const fs = require('fs');
const [baseUrl, routeDatasetFile, outputPath] = process.argv.slice(2);
const routes = JSON.parse(fs.readFileSync(routeDatasetFile, 'utf8')).routes;
const started = Date.now();
const results = [];

async function warm(route) {
  const payload = {
    start: route.start,
    end: route.end,
    profile: route.profile || 'standard',
    safetyWeight: route.safetyWeight ?? 0.5,
    preferences: route.preferences || []
  };
  const routeStarted = Date.now();
  const response = await fetch(`${baseUrl}/api/v1/routing/safe-path/options`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  });
  const body = await response.text();
  results.push({
    name: route.name || 'route',
    status: response.status,
    durationMs: Date.now() - routeStarted,
    responseBytes: body.length
  });
  if (!response.ok && response.status !== 202) {
    throw new Error(`Warmup failed for ${route.name || 'route'} with HTTP ${response.status}: ${body.slice(0, 500)}`);
  }
}

(async () => {
  for (const route of routes) {
    await warm(route);
  }
  fs.writeFileSync(outputPath, JSON.stringify({
    generatedAtUtc: new Date().toISOString(),
    totalDurationMs: Date.now() - started,
    routeCount: routes.length,
    results
  }, null, 2));
})().catch((error) => {
  console.error(error.stack || String(error));
  process.exit(1);
});
NODE
fi

echo "Running city API p99 with route dataset $ROUTE_DATASET_FILE"
if [[ "$PROFILE_FLAMEGRAPH" == "true" ]]; then
  PROFILE_ARTIFACT_DIR="$ARTIFACT_DIR/flamegraph"
  mkdir -p "$PROFILE_ARTIFACT_DIR"
  ARTIFACT_DIR="$PROFILE_ARTIFACT_DIR" \
  DURATION_SECONDS="$PROFILE_DURATION_SECONDS" \
  tools/profile-routing-api-flamegraph.sh "$API_PID" \
    >"$PROFILE_ARTIFACT_DIR/profile.log" 2>&1 &
  PROFILE_PID=$!
fi

BASE_URL="$BASE_URL" \
ROUTE_DATASET_FILE="$ROUTE_DATASET_FILE" \
DURATION="$DURATION" \
ROUTE_RATE="$ROUTE_RATE" \
ROUTE_P95_MS="$ROUTE_P95_MS" \
ROUTE_P99_MS="$ROUTE_P99_MS" \
ARTIFACT_DIR="$ARTIFACT_DIR/k6" \
tools/run-routing-api-p99-test.sh

if [[ -n "${PROFILE_PID:-}" ]]; then
  wait "$PROFILE_PID" || {
    echo "Flamegraph capture failed. See $PROFILE_ARTIFACT_DIR/profile.log" >&2
    cat "$PROFILE_ARTIFACT_DIR/profile.log" >&2 || true
    exit 1
  }
fi

node - "$ARTIFACT_DIR" "$CITY_NAME" "$OSM_URL" "$OSM_FILE" "$ROUTE_DATASET_FILE" <<'NODE'
const fs = require('fs');
const os = require('os');
const [dir, cityName, osmUrl, osmFile, routeDatasetFile] = process.argv.slice(2);
const summary = JSON.parse(fs.readFileSync(`${dir}/k6/k6-routing-api-summary.json`, 'utf8'));
const graph = JSON.parse(fs.readFileSync(`${dir}/route-graph-status.json`, 'utf8'));
const metric = (name) => summary.metrics[name] || {};
const report = {
  harnessVersion: 'accesscity-real-city-api-p99-v1',
  generatedAtUtc: new Date().toISOString(),
  cityName,
  osmUrl,
  osmFile,
  routeDatasetFile,
  machine: {
    platform: process.platform,
    arch: process.arch,
    cpus: os.cpus().length,
    totalMemoryBytes: os.totalmem()
  },
  routeGraph: graph,
  httpReqs: metric('http_reqs'),
  httpReqDurationMs: metric('http_req_duration{name:route-options}'),
  endToEndMs: metric('route_end_to_end_ms'),
  httpReqFailed: metric('http_req_failed'),
  routeApiFailure: metric('route_api_failure'),
  checks: metric('checks'),
  externalOsrmEnabled: process.env.EXTERNAL_OSRM_ENABLED === 'true',
  claimBoundary: 'Full HTTP p99 against a local AccessCity.API process, local Postgres/PostGIS, imported OSM route graph, and local rate limits raised for service-capacity measurement. External OSRM is disabled by default in this harness so p99 reflects local graph and API performance rather than public network latency.'
};
fs.writeFileSync(`${dir}/real_city_api_p99_report.json`, JSON.stringify(report, null, 2));
fs.writeFileSync(`${dir}/real_city_api_p99_report.md`, `# AccessCity Real City API p99

- City: ${cityName}
- Route graph: ${graph.routeNodeCount} nodes / ${graph.routeEdgeCount} edges
- Requests: ${report.httpReqs.count} at ${Number(report.httpReqs.rate || 0).toFixed(2)} rps
- HTTP failures: ${Number(report.httpReqFailed.value || 0).toFixed(4)}
- Route API failures: ${Number(report.routeApiFailure.value || 0).toFixed(4)}
- HTTP p95/p99: ${report.httpReqDurationMs['p(95)']} ms / ${report.httpReqDurationMs['p(99)']} ms
- End-to-end p95/p99: ${report.endToEndMs['p(95)']} ms / ${report.endToEndMs['p(99)']} ms

${report.claimBoundary}
`);
console.log(`Report written: ${dir}/real_city_api_p99_report.json`);
NODE

echo "Real city API p99 artifacts: $ARTIFACT_DIR"
