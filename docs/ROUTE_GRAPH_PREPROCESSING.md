# Route Graph Preprocessing

AccessCity now has a staged route graph preprocessing path:

- packed graph artifacts: flat node/edge arrays for Redis and worker hot-load;
- prepartitioned grid cells: nearby routes reuse stable cell artifacts instead of exact route-shaped blobs only;
- versioned edge weights: artifacts are invalidated when accessibility cost or traversal-weight logic changes;
- ALT landmarks: each non-truncated shard can carry landmark distance tables so A* gets a stronger admissible lower bound than straight-line distance alone.
- compact landmark tables: ALT distances stay in rounded `float` seconds in memory and in Redis payloads, avoiding a hot-load expansion back to `double` tables.
- binary Redis payloads: packed artifacts are stored as versioned binary bytes in L2 cache while keeping legacy gzip/JSON read compatibility.
- dense ALT preprocessing: shard preprocessing converts node ids to dense indexes and adjacency arrays before running landmark Dijkstra, avoiding repeated dictionary lookups and per-node edge allocations on city-sized bundles.
- file artifact store: packed `.acrg` payloads can be written through to a shared filesystem with JSON sidecar metadata, letting workers hot-load versioned graph artifacts after restart without rebuilding from PostGIS.
- artifact manifest: offline source shard artifacts are indexed by bbox, version, artifact-set id, byte size, and SHA-256 payload hash, so runtime graph loads can resolve and verify prebuilt cell artifacts from the manifest before falling back to PostGIS.
- release command: `--build-route-graph-release` builds a bbox into versioned shard artifacts, writes the manifest, and immediately validates every shard before the process exits. `--validate-route-graph-release` verifies an existing release without rebuilding.
- experimental CH query data: contraction hierarchies can be built for static shortest-path profiling, but safe-path requests keep A*/ALT unless the request has no hazards, no preferences, and `SafetyWeight=0`, because CH artifacts do not encode live risk or per-request comfort modifiers.

## Why ALT First

CH/CCH/CRP are the long-term target for city and region scale. They need a larger graph build pipeline and careful customization for accessibility, hazards, closures, and profile-specific weights. ALT is the safe intermediate step for AccessCity's user-facing safe-path route decisions: it is deterministic, testable, works on directed graphs, preserves exact shortest-path results when the lower bound is admissible, and can be packed into the current shard artifact.

The checked-in CH path is deliberately conservative. It now uses a directed bidirectional query with a reverse upward graph and expands shortcuts back into materialized graph edges, but `RoutingService` only uses it for static shortest-path requests. Dynamic safety routing still uses A*/ALT so hazards, `SafetyWeight`, and request preferences stay reproducible and auditable.

The checked-in implementation computes ALT landmarks over the minimum traversal-time metric (`distance / 2.0m/s`). Runtime route cost is always at least that lower bound, so the heuristic does not change route decisions; it only reduces search work. Landmark distances are rounded to milliseconds and stored as `float` seconds; query-time lower-bound comparisons still promote to `double` and subtract a quantization safety margin, but the shard hot-load path does not double the landmark table footprint.

## Profiling Real Extracts

Use the profile command against a real OSM extract before raising shard sizes or worker counts:

```bash
tools/profile-city-route-graph.sh
```

The script downloads the BBBike Birmingham `.osm.pbf` extract if `data/osm/birmingham.osm.pbf` is absent, then runs the API in an offline profile-and-exit mode. This path builds the routing graph, shard index, packed artifact, ALT tables, and binary Redis payload without importing the city graph into PostGIS first. Override the file path or cap when needed:

```bash
OSM_FILE=data/osm/birmingham.osm.pbf \
Routing__MaxRouteGraphEdges=2000000 \
tools/profile-city-route-graph.sh
```

The JSON result reports source graph size, source shard count, shard reuse ratio, uncompressed artifact size, binary Redis payload bytes, cold shard merge/preprocessing time, worker hot-load time from Redis payload restore, artifact pack time, and artifact unpack time.

When `Routing__RouteGraphFileArtifactStoreEnabled=true`, the profile also persists packed artifacts under `data/route-graph-artifacts` via the compose mount. With `Routing__RouteGraphOfflineShardArtifactBuildEnabled=true`, it writes one versioned artifact per offline source shard before route-level bundle profiling and publishes `manifest.json` with shard bbox, payload size, SHA-256 payload hash, artifact-set id, and version metadata. Use `Routing__RouteGraphOfflineShardArtifactBuildLimit` for quick smoke runs; `0` means all shards.

When `Routing__RouteGraphFileArtifactManifestEnabled=true`, readiness checks the manifest version and verifies a bounded sample of the largest shard artifacts with `Routing__RouteGraphFileArtifactReadinessValidationShardLimit` before the pod is considered ready. Worker-side `Routing__RouteGraphFileArtifactWarmupEnabled=true` reads the same manifest and only writes verified payloads into Redis, so a partial or corrupt artifact publish is detected before route traffic depends on it.

To build a release from PostGIS and fail the job if any artifact is corrupt or incompatible:

```bash
Routing__RouteGraphFileArtifactStoreEnabled=true \
Routing__RouteGraphReleaseMinLon=-1.95 \
Routing__RouteGraphReleaseMinLat=52.43 \
Routing__RouteGraphReleaseMaxLon=-1.86 \
Routing__RouteGraphReleaseMaxLat=52.51 \
dotnet run --project AccessCity.API/AccessCity.API.csproj -- --build-route-graph-release
```

If explicit release bounds are omitted, the command derives a bbox from `Routing__RouteGraphWarmupRoutes` plus `Routing__RouteGraphReleasePaddingDegrees`. Validate an already-published artifact directory with:

```bash
dotnet run --project AccessCity.API/AccessCity.API.csproj -- --validate-route-graph-release
```

Latest Birmingham extract check (`Birmingham.osm.pbf`, 53.6MB, `Routing__MaxRouteGraphEdges=2000000`) built a non-truncated graph of 661,852 nodes and 1,428,512 directed edges into 1,419 shards. The offline shard artifact build persisted all 1,419 source shard artifacts plus `manifest.json` in about 7.4s, totaling about 225.7MB of binary `.acrg` payloads. The four warmup routes reused 53 unique shards across 89 references (`shardReuseRatio=0.4045`), all carried ALT-v1 preprocessing, and the largest route artifact moved from about 69.5MB JSON to about 14.6MB binary Redis payload. With the shard index, compact in-memory ALT tables, dense preprocessing graph, binary payload pre-sizing, file artifact store, and manifest in place, max cold shard merge/preprocessing time was about 150ms, max production pack/binary serialize time was about 73ms, max artifact unpack time was about 54ms, max Redis hot-load restore was about 46ms, and max file artifact hot-load was about 56ms on the local offline extract profile.

## Runtime PostGIS Import Profile

The runtime PostGIS path is still not the preferred serving path for 1M+ DAU, but import now uses PostgreSQL binary `COPY` for route nodes, route edges, and infrastructure assets when `OsmImport__UsePostgresCopy=true`. Keep this enabled on workers; the EF `SaveChanges` path remains as a fallback for tests or non-Postgres providers.

Validated local Birmingham runtime import (`Routing__RouteGraphProfileUseOsmExtract=false`, `Routing__MaxRouteGraphEdges=2000000`, `OsmImport__BulkCopyBatchSize=20000`) inserted 661,852 route nodes, 1,429,002 directed route edges, and 202,230 infrastructure assets in 80.68 seconds. The resulting PostGIS profile produced four ALT-backed route graph artifacts with 89 shard references, largest binary Redis payload about 14.6MB, max cold repository load about 3.43s, and hot in-memory loads near 0ms once the shard bundle was cached.

Use this mode to validate schema and runtime graph loading after import changes:

```bash
Routing__RouteGraphProfileUseOsmExtract=false \
OsmImport__BulkCopyBatchSize=20000 \
Routing__MaxRouteGraphEdges=2000000 \
tools/profile-city-route-graph.sh
```

If runtime import regresses, check `feed_ingestion_runs` duration, Postgres WAL/write I/O, and whether `OsmImport__UsePostgresCopy` was disabled. For production-scale serving, prefer prebuilt immutable graph artifacts and worker warmup over having every route shard depend on fresh PostGIS reads.

## Reading Results

- `shardReuseRatio`: should increase as warmup/profile routes overlap. Low values mean route requests are too dispersed for exact route cache to matter and the graph needs larger precomputed partitions or route bucketing.
- `artifactBytes` / `redisPayloadBytes`: `artifactBytes` is uncompressed JSON; `redisPayloadBytes` is the binary L2 payload. Watch both before raising landmark count. ALT tables scale with `nodes * landmarks * 2`, and the production in-memory tables use 4-byte floats.
- `artifactUnpackMilliseconds` / `hotLoadMilliseconds`: proxy for new worker hot-load time from Redis, including binary payload restore.
- `isTruncated`: any `true` profile route means the route graph cap is too low or the bbox is too broad for the current shard settings.
- `hasAltPreprocessing`: should be true for non-truncated shards under `RouteGraphMaxAltPreprocessedNodes`.

## Next Preprocessing Layer

For 1M+ DAU, the next substantial step is a dedicated city graph build artifact outside request workers:

1. Run the offline shard artifact build in CI/CD or a data pipeline, publish the `.acrg` directory and `manifest.json` to a shared volume/object store, then start API/worker pods with `RouteGraphFileArtifactStoreEnabled=true`.
2. Store immutable graph/weight artifacts with explicit version ids, payload checksums, and readiness-gated worker warmup before traffic.
3. Add a customization phase for accessibility costs, temporary closures, and hazard overlays.
4. Move from ALT to CCH or CRP for larger city/region graphs once the weight customization model is stable.
