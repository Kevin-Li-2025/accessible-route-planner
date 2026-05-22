# Distributed Load Testing

This repo includes a Kubernetes-native k6 job for testing the real multi-replica path:

- `accesscity-api` deployment with multiple replicas
- dedicated worker deployment
- Kafka-backed messaging
- Redis-backed distributed cache
- Postgres/PostGIS connection pools

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

- `/health` and `/health/ready` for replica and dependency readiness.
- `/api/v1/routing/risk-score` with slightly varied coordinates to exercise scoped PostGIS hazard queries.
- `/api/v1/routing/safe-path` every fourth iteration to exercise OSRM fallback, route graph loading, route cache, coalescing, and the route computation limiter.

Expected threshold defaults:

- overall HTTP failure rate below 2%
- overall p95 below 750 ms and p99 below 2 s
- `safe-path` p95 below 2.5 s
- `risk-score` p95 below 250 ms

If `safe-path` returns `503` or `504` during high bursts, that is capacity protection working rather than silent overload. Increase API replicas, `Routing__MaxConcurrentComputations`, Postgres pool size, and CPU only after checking DB wait time and CPU saturation.

## Tuning Knobs

- `Postgres__MaxPoolSize`: Npgsql physical connection pool per pod.
- `Postgres__DbContextPoolSize`: EF Core context pool per pod.
- `Routing__MaxConcurrentComputations`: per-pod CPU gate for A*/route scoring work.
- `Routing__MaxRouteGraphEdges`: cap on route graph fanout before falling back.
- `Routing__MaxHazardsPerRequest`: cap on active hazards loaded for one route/risk request.
- `ExternalApis__*__MaxConcurrentRequests`: per-pod bulkhead for tail-sensitive upstream services.
- `ExternalApis__CircuitBreaker__*`: shared timeout/circuit behavior for external dependency fallback.
