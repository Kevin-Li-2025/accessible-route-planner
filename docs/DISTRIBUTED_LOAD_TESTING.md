# Distributed Load Testing

This repo includes a Kubernetes-native k6 job for testing the real multi-replica path:

- `accesscity-api` deployment with multiple replicas
- dedicated worker deployment
- Kafka-backed messaging
- Redis-backed distributed cache
- Postgres/PostGIS connection pools
- PgBouncer/CloudNativePG pooler when `DATABASE_URL` points at the pooler service

The load test is intentionally not part of the default kustomization, so normal deploys do not create traffic.

## Run

```bash
kubectl apply -k deploy/kubernetes
kubectl -n accesscity rollout status deploy/accesscity-api
kubectl -n accesscity rollout status deploy/accesscity-worker

kubectl -n accesscity apply -f deploy/kubernetes/loadtest-configmap.yaml
kubectl -n accesscity delete job accesscity-distributed-loadtest --ignore-not-found
kubectl -n accesscity apply -f deploy/kubernetes/loadtest-job.yaml
kubectl -n accesscity logs -f job/accesscity-distributed-loadtest
```

## What It Exercises

The script mixes:

- `/health/ready` at a fixed arrival rate to catch readiness tail regressions.
- `/api/v1/routing/risk-score` with varied coordinates to exercise scoped PostGIS hazard queries and risk cache bucketing.
- `/api/v1/spatial/poi` with varied coordinates to exercise POI PostGIS/cache hot paths.
- `/api/v1/routing/safe-path/options` as async route jobs, then polls `/api/v1/routing/jobs/{jobId}` to verify the real API -> Kafka -> worker -> Redis status path.

In production config, `safe-path` can return `202 Accepted` for cache misses. That is the expected
async-first path; the job result is stored through the distributed cache so polling can land on any
API replica.

Expected threshold defaults:

- overall HTTP failure rate below 2%
- overall p95 below 1 s and p99 below 3 s
- route job submission p95 below 450 ms
- route job polling p95 below 250 ms
- `risk-score` p95 below 250 ms
- `spatial/poi` p95 below 350 ms
- readiness p95 below 150 ms

If `safe-path` returns `503` or `504` during high bursts, that is capacity protection working rather than silent overload. Increase API replicas, `Routing__MaxConcurrentComputations`, Postgres pool size, and CPU only after checking DB wait time and CPU saturation.

In Kubernetes, the default kustomization now lets KEDA scale `accesscity-api` from safe-path p95
latency, route limiter saturation, and Prometheus-backed CPU/memory queries. If Prometheus runs
outside the `accesscity` namespace, update `deploy/kubernetes/keda-scaledobject.yaml` before applying.

After this short run passes, use `docs/SOAK_AND_CHAOS_TESTING.md` for the 24-hour soak and failure
injection checks.

## Tuning Knobs

- `Postgres__MaxPoolSize`: Npgsql physical connection pool per pod.
  Keep this low when the app points at PgBouncer; total client connections should scale with API
  replicas without turning into hundreds of direct Postgres backends.
- `Kafka__TopicPartitions`: production manifests default to 48 so route jobs can spread across
  tens of workers; keep broker count, partition count, and worker max scale aligned.
- `Postgres__DbContextPoolSize`: EF Core context pool per pod.
- `Routing__MaxConcurrentComputations`: per-pod CPU gate for A*/route scoring work.
- `Routing__MaxRouteGraphEdges`: cap on route graph fanout before falling back.
- `Routing__RouteGraphPrepartitionedShardsEnabled`: split route graph loads into reusable grid
  cell artifacts instead of caching only exact route-sized graph blobs.
- `Routing__RouteGraphPackedArtifactsEnabled`: store compact versioned graph artifacts with
  precomputed edge traversal weights in the shared cache.
- `Routing__MaxHazardsPerRequest`: cap on active hazards loaded for one route/risk request.
- `ExternalApis__*__MaxConcurrentRequests`: per-pod bulkhead for tail-sensitive upstream services.
- `ExternalApis__CircuitBreaker__*`: shared timeout/circuit behavior for external dependency fallback.
- `accesscity_route_computation_saturated_total`: increase means route CPU slots are exhausted.
- `accesscity_external_dependency_fallback_total`: increase means OSRM/Overpass/Police/environmental calls are degrading to fallback.
