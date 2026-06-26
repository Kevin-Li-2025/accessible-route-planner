#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

CITY_NAME="${CITY_NAME:-west-midlands}"
OSM_URL="${OSM_URL:-https://download.geofabrik.de/europe/united-kingdom/england/west-midlands-latest.osm.pbf}"
ROUTE_DATASET_FILE="${ROUTE_DATASET_FILE:-tools/k6/birmingham-city-routes.json}"
DURATION="${DURATION:-60s}"
ROUTE_RATES="${ROUTE_RATES:-4 16 64 128}"
ROUTE_P95_MS="${ROUTE_P95_MS:-500}"
ROUTE_P99_MS="${ROUTE_P99_MS:-1500}"
ARTIFACT_DIR="${ARTIFACT_DIR:-TestResults/accesscity-real-city-api-p99-matrix}"
IMPORT_ON_FIRST_RUN="${IMPORT_ON_FIRST_RUN:-false}"
PROFILE_FIRST_RATE="${PROFILE_FIRST_RATE:-true}"

mkdir -p "$ARTIFACT_DIR"

first_run=true
summary_json="$ARTIFACT_DIR/api_p99_matrix_summary.json"
summary_md="$ARTIFACT_DIR/api_p99_matrix_summary.md"
printf '[]' > "$summary_json"

for rate in $ROUTE_RATES; do
  rate_dir="$ARTIFACT_DIR/rate-${rate}"
  skip_import=true
  if [[ "$first_run" == "true" && "$IMPORT_ON_FIRST_RUN" == "true" ]]; then
    skip_import=false
  fi

  profile=false
  if [[ "$first_run" == "true" && "$PROFILE_FIRST_RATE" == "true" ]]; then
    profile=true
  fi

  echo "Running ${CITY_NAME} API p99 at ${rate} rps"
  CITY_NAME="$CITY_NAME" \
  OSM_URL="$OSM_URL" \
  ROUTE_DATASET_FILE="$ROUTE_DATASET_FILE" \
  DURATION="$DURATION" \
  ROUTE_RATE="$rate" \
  ROUTE_P95_MS="$ROUTE_P95_MS" \
  ROUTE_P99_MS="$ROUTE_P99_MS" \
  PROFILE_FLAMEGRAPH="$profile" \
  PROFILE_DURATION_SECONDS="${DURATION%s}" \
  SKIP_IMPORT="$skip_import" \
  ARTIFACT_DIR="$rate_dir" \
  tools/run-real-city-api-p99.sh

  node - "$summary_json" "$rate" "$rate_dir" <<'NODE'
const fs = require('fs');
const [summaryPath, rate, dir] = process.argv.slice(2);
const rows = JSON.parse(fs.readFileSync(summaryPath, 'utf8'));
const report = JSON.parse(fs.readFileSync(`${dir}/real_city_api_p99_report.json`, 'utf8'));
rows.push({
  routeRate: Number(rate),
  requests: report.httpReqs.count,
  achievedRps: report.httpReqs.rate,
  failures: report.httpReqFailed.value,
  p50Ms: report.httpReqDurationMs.med,
  p95Ms: report.httpReqDurationMs['p(95)'],
  p99Ms: report.httpReqDurationMs['p(99)'],
  maxMs: report.httpReqDurationMs.max,
  routeNodes: report.routeGraph.routeNodeCount,
  routeEdges: report.routeGraph.routeEdgeCount,
  artifactDir: dir
});
fs.writeFileSync(summaryPath, JSON.stringify(rows, null, 2));
NODE

  first_run=false
done

node - "$summary_json" "$summary_md" <<'NODE'
const fs = require('fs');
const [summaryPath, mdPath] = process.argv.slice(2);
const rows = JSON.parse(fs.readFileSync(summaryPath, 'utf8'));
const lines = [
  '# AccessCity Real City API p99 Matrix',
  '',
  '| Target rps | Requests | Achieved rps | Failures | p50 ms | p95 ms | p99 ms | max ms | Artifacts |',
  '| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |'
];
for (const row of rows) {
  lines.push(`| ${row.routeRate} | ${row.requests} | ${Number(row.achievedRps).toFixed(2)} | ${Number(row.failures).toFixed(4)} | ${Number(row.p50Ms).toFixed(2)} | ${Number(row.p95Ms).toFixed(2)} | ${Number(row.p99Ms).toFixed(2)} | ${Number(row.maxMs).toFixed(2)} | ${row.artifactDir} |`);
}
fs.writeFileSync(mdPath, `${lines.join('\n')}\n`);
NODE

echo "Matrix summary: $summary_json"
echo "Matrix markdown: $summary_md"
