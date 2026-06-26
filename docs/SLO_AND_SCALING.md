# SLO and Scaling Guardrails

AccessCity scales on application pressure, not only CPU:

- API replicas scale with KEDA from Prometheus-backed CPU, memory, safe-path p95 latency, and route computation saturation.
- Worker replicas scale from Kafka lag for route and OSM import topics; the checked-in production path allows 6 to 100 workers and 48 Kafka partitions.
- Route computation uses an in-process bulkhead per API pod; saturated requests return `503` instead of queuing unbounded work.
- External dependencies use timeout, bulkhead, circuit-breaker fallback, and metrics per dependency.
- Production safe-path cache misses return `202 Accepted` by default and compute in the background; completed job status is published through the distributed cache so polling works across API replicas.
- Production risk scoring uses cached external signals only. Public Overpass, Police, environmental, and weather APIs stay off the request hot path by default.

## Production SLOs

| Path | Target | Alert |
| --- | --- | --- |
| `/api/v*/routing/safe-path/options` | p95 below 250ms and p99 below 1s for warmed graph/local dependency paths | `AccessCityRouteOptionsP99TooSlow` |
| `/api/v*/routing/safe-path` | p95 below 1.5s over 5 minutes for full dependency path | `AccessCitySafePathP95TooSlow` |
| API 5xx rate | below 1% over 5 minutes | `AccessCityHighErrorRate` |
| route computation saturation | effectively zero sustained rejects | `AccessCityRouteCapacitySaturated` |
| external dependency fallback rate | below 5% over 5 minutes | `AccessCityExternalDependencyFallbackSpike` |
| shared cache hit ratio | above 70% over 15 minutes | `AccessCityLowCacheHitRatio` |

## Local SLO Gate

After running the k6 API p99 harness, convert the summary into a machine-readable SLO verdict:

```bash
SLO_ROUTE_P95_MS=250 \
SLO_ROUTE_P99_MS=1000 \
SLO_ROUTE_FAILURE_RATE=0.001 \
node tools/evaluate-routing-slo.js \
  TestResults/accesscity-routing-api-p99/k6-routing-api-summary.json \
  TestResults/accesscity-routing-api-p99/routing_slo_report.json
```

This gate is intentionally stricter than the broad production safe-path SLO because it targets a warmed
route graph and local dependency path. Use the full production SLO for internet OSRM/provider paths.

## Prometheus Metrics

| Metric | Meaning |
| --- | --- |
| `accesscity_route_safe_path_duration_milliseconds_*` | safe-path and route-options latency by route and outcome |
| `accesscity_route_computation_queue_duration_milliseconds_*` | time spent waiting for route CPU capacity |
| `accesscity_route_computation_saturated_total` | requests rejected by the per-pod route bulkhead |
| `accesscity_route_computation_inflight` | route computations currently using CPU capacity |
| `accesscity_route_coalescing_total` | duplicate safe-path request coalescing outcomes |
| `accesscity_route_graph_load_duration_milliseconds_*` | route graph shard load latency by memory, distributed cache, file artifact, or PostGIS source |
| `accesscity_route_graph_load_edges_edges_*` | loaded edge count distribution by route graph source and outcome |
| `accesscity_route_graph_load_total` | route graph load attempts by source and outcome |
| `accesscity_external_dependency_duration_milliseconds_*` | guarded external call latency by dependency and outcome |
| `accesscity_external_dependency_fallback_total` | fallback usage by dependency and reason |
| `accesscity_external_dependency_circuit_opened_total` | circuit breaker open events |

Grafana starter dashboard:

```text
deploy/observability/grafana-accesscity-performance-dashboard.json
```

The dashboard tracks route p95/p99, graph load p95/p99 by source, cache hit/miss pressure,
loaded edge counts, and route computation saturation. Use it with the OTEL collector Prometheus
exporter in `deploy/observability/otel-collector-config.yaml`.

## Deployment Notes

`deploy/kubernetes/keda-scaledobject.yaml` expects KEDA and a Prometheus service reachable as
`http://prometheus:9090` from the `accesscity` namespace. If Prometheus runs elsewhere, update
the `serverAddress` fields before applying the kustomization.

The API KEDA object includes a 6-replica fallback if Prometheus-backed scalers fail repeatedly,
plus weekday morning and evening cron triggers that hold at least 10 replicas during expected peak
traffic windows. CPU and memory are Prometheus queries rather than KEDA resource triggers because
KEDA fallback does not support the built-in CPU/memory scalers.

Route workers scale from Kafka lag on `accesscity_routejobrequestedevent` and
`accesscity_osmimportstartedevent`. Keep the partition count at or above the intended active worker
count for route jobs; otherwise Kafka partition ownership, not CPU, becomes the worker scale limit.

PostGIS hot reads should go through PgBouncer and, when available, a read replica. The production
config enables `Postgres__UseReadOnlyForHotPaths`; set `READONLY_DATABASE_URL` for route graph
shard loads to stop competing with writes, migrations, and ingestion on the primary.

Route graph cache misses should create reusable graph partitions, not one-off route-sized blobs.
The production config enables `Routing__RouteGraphPrepartitionedShardsEnabled` and
`Routing__RouteGraphPackedArtifactsEnabled`, so workers cache packed grid-cell artifacts with
explicit edge-weight versions. `Routing__RouteGraphAltPreprocessingEnabled` adds packed ALT
landmark tables for non-truncated shards, improving A* pruning without changing route decisions.
Watch packed artifact hit ratio, ALT artifact bytes, and artifact unpack time before increasing
route worker counts; without reusable graph partitions, worker scale mostly moves the CPU
bottleneck around.

The API KEDA object replaces the standalone `hpa.yaml` in the default kustomization so only one
controller owns the `accesscity-api` deployment scale. Keep `hpa.yaml` as a CPU/memory fallback
for environments that do not run KEDA Prometheus triggers.
