#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

mkdir -p data/osm
mkdir -p data/route-graph-artifacts
chmod ugo+rwx data/route-graph-artifacts

BBOX="${BBOX:-52.45,-1.94,52.53,-1.84}"
OSM_URL="${OSM_URL:-https://download.bbbike.org/osm/bbbike/Birmingham/Birmingham.osm.pbf}"
OSM_FILE="${OSM_FILE:-data/osm/birmingham.osm.pbf}"
OSM_CONTAINER_PATH="/app/osm-import/$(basename "$OSM_FILE")"
OVERPASS_URL="${OVERPASS_URL:-https://overpass.osm.ch/api/interpreter}"

if [[ ! -s "$OSM_FILE" ]]; then
  if [[ -n "$OSM_URL" ]]; then
    echo "Downloading real OSM extract into $OSM_FILE"
    curl --fail --show-error --location --compressed --retry 3 \
      "$OSM_URL" \
      --output "$OSM_FILE"
  else
    echo "Downloading real OSM extract from Overpass into $OSM_FILE"
    read -r -d '' QUERY <<EOF || true
[out:xml][timeout:240];
(
  way["highway"]($BBOX);
  node["barrier"]($BBOX);
  node["highway"~"^(crossing|bus_stop|elevator)$"]($BBOX);
);
(._;>;);
out body;
EOF
    curl --fail --show-error --location --compressed --retry 3 \
      --data-urlencode "data=${QUERY}" \
      "$OVERPASS_URL" \
      --output "$OSM_FILE"
  fi
fi

if [[ "${Routing__RouteGraphProfileUseOsmExtract:-true}" == "true" ]]; then
  echo "Skipping PostGIS/Redis startup; offline OSM extract profile does not need runtime services"
else
  echo "Starting PostGIS and Redis"
  docker compose up -d db redis
fi

echo "Building API image"
docker compose build api

echo "Running route graph profile command"
docker compose run --rm --no-deps \
  -e Messaging__UseKafka=false \
  -e Postgres__AutoMigrate=false \
  -e Postgres__AutoSchemaMaintenance=false \
  -e OsmImport__ReplaceExisting=true \
  -e OsmImport__FilePath="$OSM_CONTAINER_PATH" \
  -e OsmImport__ImportOnStartup=false \
  -e Routing__RouteGraphProfileAndExit=true \
  -e Routing__RouteGraphPrepartitionedShardsEnabled=true \
  -e Routing__RouteGraphPackedArtifactsEnabled=true \
  -e Routing__RouteGraphMaxDistributedSnapshotBytes="${Routing__RouteGraphMaxDistributedSnapshotBytes:-8388608}" \
  -e Routing__RouteGraphFileArtifactStoreEnabled="${Routing__RouteGraphFileArtifactStoreEnabled:-true}" \
  -e Routing__RouteGraphFileArtifactDirectory="${Routing__RouteGraphFileArtifactDirectory:-/app/route-graph-artifacts}" \
  -e Routing__RouteGraphFileArtifactWriteThroughEnabled="${Routing__RouteGraphFileArtifactWriteThroughEnabled:-true}" \
  -e Routing__RouteGraphFileArtifactManifestEnabled="${Routing__RouteGraphFileArtifactManifestEnabled:-true}" \
  -e Routing__RouteGraphFileArtifactManifestFileName="${Routing__RouteGraphFileArtifactManifestFileName:-manifest.json}" \
  -e Routing__RouteGraphMaxFileArtifactShardLoadCount="${Routing__RouteGraphMaxFileArtifactShardLoadCount:-64}" \
  -e Routing__RouteGraphOfflineShardArtifactBuildEnabled="${Routing__RouteGraphOfflineShardArtifactBuildEnabled:-true}" \
  -e Routing__RouteGraphOfflineShardArtifactBuildLimit="${Routing__RouteGraphOfflineShardArtifactBuildLimit:-0}" \
  -e Routing__RouteGraphAltPreprocessingEnabled=true \
  -e Routing__RouteGraphAltLandmarkCount="${Routing__RouteGraphAltLandmarkCount:-4}" \
  -e Routing__RouteGraphMaxAltPreprocessedNodes="${Routing__RouteGraphMaxAltPreprocessedNodes:-60000}" \
  -e Routing__RouteGraphProfileUseOsmExtract="${Routing__RouteGraphProfileUseOsmExtract:-true}" \
  -e Routing__RouteGraphProfileOutputPath="${Routing__RouteGraphProfileOutputPath:-/app/route-graph-artifacts/profile-report.json}" \
  -e Routing__RouteGraphProfileFailOnQualityGate="${Routing__RouteGraphProfileFailOnQualityGate:-false}" \
  -e Routing__RouteGraphProfileMaxRedisPayloadBytes="${Routing__RouteGraphProfileMaxRedisPayloadBytes:-8388608}" \
  -e Routing__RouteGraphProfileMaxArtifactBytes="${Routing__RouteGraphProfileMaxArtifactBytes:-33554432}" \
  -e Routing__RouteGraphProfileMaxColdLoadMilliseconds="${Routing__RouteGraphProfileMaxColdLoadMilliseconds:-2000}" \
  -e Routing__RouteGraphProfileMaxHotLoadMilliseconds="${Routing__RouteGraphProfileMaxHotLoadMilliseconds:-100}" \
  -e Routing__RouteGraphProfileMaxArtifactPackMilliseconds="${Routing__RouteGraphProfileMaxArtifactPackMilliseconds:-750}" \
  -e Routing__RouteGraphProfileMaxArtifactStoreReadMilliseconds="${Routing__RouteGraphProfileMaxArtifactStoreReadMilliseconds:-150}" \
  -e Routing__RouteGraphProfileMaxArtifactUnpackMilliseconds="${Routing__RouteGraphProfileMaxArtifactUnpackMilliseconds:-150}" \
  -e Routing__RouteGraphProfileMaxShardReferencesPerRoute="${Routing__RouteGraphProfileMaxShardReferencesPerRoute:-64}" \
  -e Routing__MaxRouteGraphEdges="${Routing__MaxRouteGraphEdges:-2000000}" \
  -e Routing__RouteGraphWarmupRoutes__0__Name=birmingham-core \
  -e Routing__RouteGraphWarmupRoutes__0__StartLat=52.4862 \
  -e Routing__RouteGraphWarmupRoutes__0__StartLng=-1.8904 \
  -e Routing__RouteGraphWarmupRoutes__0__EndLat=52.4862 \
  -e Routing__RouteGraphWarmupRoutes__0__EndLng=-1.8894 \
  -e Routing__RouteGraphWarmupRoutes__1__Name=birmingham-cross-city \
  -e Routing__RouteGraphWarmupRoutes__1__StartLat=52.4814 \
  -e Routing__RouteGraphWarmupRoutes__1__StartLng=-1.8985 \
  -e Routing__RouteGraphWarmupRoutes__1__EndLat=52.4510 \
  -e Routing__RouteGraphWarmupRoutes__1__EndLng=-1.9300 \
  -e Routing__RouteGraphWarmupRoutes__2__Name=birmingham-east-west \
  -e Routing__RouteGraphWarmupRoutes__2__StartLat=52.4835 \
  -e Routing__RouteGraphWarmupRoutes__2__StartLng=-1.8885 \
  -e Routing__RouteGraphWarmupRoutes__2__EndLat=52.4795 \
  -e Routing__RouteGraphWarmupRoutes__2__EndLng=-1.8936 \
  -e Routing__RouteGraphWarmupRoutes__3__Name=birmingham-jewellery-core \
  -e Routing__RouteGraphWarmupRoutes__3__StartLat=52.4855 \
  -e Routing__RouteGraphWarmupRoutes__3__StartLng=-1.9125 \
  -e Routing__RouteGraphWarmupRoutes__3__EndLat=52.4805 \
  -e Routing__RouteGraphWarmupRoutes__3__EndLng=-1.9015 \
  api --profile-route-graph-and-exit

echo "Route graph profile report: data/route-graph-artifacts/profile-report.json"
