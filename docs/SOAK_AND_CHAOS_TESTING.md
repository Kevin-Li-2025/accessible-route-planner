# Soak and Chaos Testing

Use the local production-mixed soak before Kubernetes if you are trying to expose p99, memory, or
restart-recovery issues quickly on one API process:

```bash
DURATION=30m \
ROUTE_COUNT=1000 \
SKIP_IMPORT=false \
WARMUP_ROUTE_CACHE=true \
tools/run-production-mixed-soak.sh
```

For a 1-hour local failure-injection run:

```bash
DURATION=1h \
ROUTE_COUNT=1000 \
FAILURE_INJECTION=api-restart \
FAILURE_AT_SECONDS=900 \
SKIP_IMPORT=true \
WARMUP_ROUTE_CACHE=true \
tools/run-production-mixed-soak.sh
```

The local harness writes k6 p50/p95/p99, failure counters, route-pair count, route graph status,
API logs, and CPU/RSS/VSZ time series under `TestResults/accesscity-production-soak/`.

Use the Kubernetes soak after the short distributed k6 run passes. The soak job keeps steady pressure on the real Kubernetes path for 24 hours:

- API replicas behind the service
- Redis-backed distributed cache
- Kafka-backed worker path
- Postgres/PostGIS connection pools
- async-first safe-path cache miss handling

## Run the Soak Test

```bash
kubectl apply -k deploy/kubernetes
kubectl -n accesscity rollout status deploy/accesscity-api
kubectl -n accesscity rollout status deploy/accesscity-worker

kubectl -n accesscity apply -f deploy/kubernetes/soaktest-configmap.yaml
kubectl -n accesscity delete job accesscity-soaktest --ignore-not-found
kubectl -n accesscity apply -f deploy/kubernetes/soaktest-job.yaml
kubectl -n accesscity logs -f job/accesscity-soaktest
```

To tune the load, change `SOAK_DURATION` and `SOAK_RATE` in `deploy/kubernetes/soaktest-job.yaml`, then delete and recreate the job.

## Success Criteria

- overall HTTP failure rate below 2%
- overall p95 below 1s and p99 below 3s
- `risk-score` p95 below 250ms
- `safe-path` p95 below 2.5s
- route job polling returns `200` when `safe-path` returns `202`
- no sustained Postgres connection pool exhaustion
- no sustained Kafka lag after worker recovery
- no API pod restart loop or memory growth trend

## Chaos Checks

Run these only in staging or a disposable namespace.

Delete one API pod while the soak test is running:

```bash
kubectl -n accesscity delete pod -l app=accesscity-api --field-selector=status.phase=Running --wait=false
kubectl -n accesscity rollout status deploy/accesscity-api
```

Expected result: a short blip at most; `safe-path` cache misses return `202`, and route job status remains visible through Redis even when polling lands on a different API pod.

Pause workers for 5 minutes:

```bash
kubectl -n accesscity scale deploy/accesscity-worker --replicas=0
sleep 300
kubectl -n accesscity scale deploy/accesscity-worker --replicas=1
```

Expected result: Kafka lag grows while workers are paused, then KEDA scales workers back out and lag drains without API latency collapse.

Force external dependency degradation in staging:

```bash
kubectl -n accesscity set env deploy/accesscity-api ExternalApis__Overpass__RealtimeHazardsEnabled=false
kubectl -n accesscity set env deploy/accesscity-api RiskScoring__RealtimeExternalSignalsEnabled=false
kubectl -n accesscity rollout status deploy/accesscity-api
```

Expected result: API traffic stays bounded by local PostGIS/cache work; public Overpass, Police, environmental, and weather APIs do not sit on the request hot path.
