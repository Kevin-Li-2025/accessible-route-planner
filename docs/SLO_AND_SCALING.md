# SLO and Scaling Guardrails

AccessCity scales on application pressure, not only CPU:

- API replicas scale with KEDA from Prometheus-backed CPU, memory, safe-path p95 latency, and route computation saturation.
- Worker replicas scale from Kafka lag for `accesscity_osmimportstartedevent`.
- Route computation uses an in-process bulkhead per API pod; saturated requests return `503` instead of queuing unbounded work.
- External dependencies use timeout, bulkhead, circuit-breaker fallback, and metrics per dependency.
- Production safe-path cache misses return `202 Accepted` by default and compute in the background; completed job status is published through the distributed cache so polling works across API replicas.
- Production risk scoring uses cached external signals only. Public Overpass, Police, environmental, and weather APIs stay off the request hot path by default.

## Production SLOs

| Path | Target | Alert |
| --- | --- | --- |
| `/api/v*/routing/safe-path` | p95 below 1.5s over 5 minutes | `AccessCitySafePathP95TooSlow` |
| API 5xx rate | below 1% over 5 minutes | `AccessCityHighErrorRate` |
| route computation saturation | effectively zero sustained rejects | `AccessCityRouteCapacitySaturated` |
| external dependency fallback rate | below 5% over 5 minutes | `AccessCityExternalDependencyFallbackSpike` |
| shared cache hit ratio | above 70% over 15 minutes | `AccessCityLowCacheHitRatio` |

## Prometheus Metrics

| Metric | Meaning |
| --- | --- |
| `accesscity_route_safe_path_duration_milliseconds_*` | safe-path and route-options latency by route and outcome |
| `accesscity_route_computation_queue_duration_milliseconds_*` | time spent waiting for route CPU capacity |
| `accesscity_route_computation_saturated_total` | requests rejected by the per-pod route bulkhead |
| `accesscity_route_computation_inflight` | route computations currently using CPU capacity |
| `accesscity_route_coalescing_total` | duplicate safe-path request coalescing outcomes |
| `accesscity_external_dependency_duration_milliseconds_*` | guarded external call latency by dependency and outcome |
| `accesscity_external_dependency_fallback_total` | fallback usage by dependency and reason |
| `accesscity_external_dependency_circuit_opened_total` | circuit breaker open events |

## Deployment Notes

`deploy/kubernetes/keda-scaledobject.yaml` expects KEDA and a Prometheus service reachable as
`http://prometheus:9090` from the `accesscity` namespace. If Prometheus runs elsewhere, update
the `serverAddress` fields before applying the kustomization.

The API KEDA object includes a 6-replica fallback if Prometheus-backed scalers fail repeatedly,
plus weekday morning and evening cron triggers that hold at least 10 replicas during expected peak
traffic windows. CPU and memory are Prometheus queries rather than KEDA resource triggers because
KEDA fallback does not support the built-in CPU/memory scalers.

The API KEDA object replaces the standalone `hpa.yaml` in the default kustomization so only one
controller owns the `accesscity-api` deployment scale. Keep `hpa.yaml` as a CPU/memory fallback
for environments that do not run KEDA Prometheus triggers.
