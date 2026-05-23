# Multi-Instance Deployment

## Local Single-Node Mode

Use the in-memory bus only for local development:

```bash
MESSAGING_USE_KAFKA=false docker compose up api
```

In this mode queued jobs are process-local and should not be used for scaled API replicas.

## Worker/Kafka Mode

Run the one-shot schema migration first, then run API replicas with Kafka enabled and OSM/tile
workers disabled on API containers:

```bash
docker compose --profile migrate run --rm migrate

MESSAGING_USE_KAFKA=true \
Routing__DispatchJobsToWorker=true \
Postgres__AutoMigrate=false \
Postgres__AutoSchemaMaintenance=false \
API_OSM_WORKER_ENABLED=false \
API_ROUTING_WORKER_ENABLED=false \
API_TILE_WORKER_ENABLED=false \
docker compose --profile worker up --scale api=2 --scale worker=2 api api-gateway worker kafka redis db
```

The API publishes `RouteJobRequestedEvent` and `OsmImportStartedEvent` messages to Kafka. Worker
containers consume the topics through the shared `accesscity-workers` consumer group, so only one
worker processes each route/import job even when multiple workers are running.
The Kafka container uses the official `apache/kafka` image in single-node KRaft mode for local
smoke tests; the app also ensures main/retry/DLQ topics exist before publishing or consuming.
Kubernetes defaults create 48 partitions and let route workers scale up to 100 pods; use a real
multi-broker Kafka cluster before running that ceiling in production.

Kafka consumers now use bounded retry and DLQ routing:

- route job main topic: `accesscity_routejobrequestedevent`
- route job retry topic: `accesscity_routejobrequestedevent.retry`
- route job dead-letter topic: `accesscity_routejobrequestedevent.dlq`
- main topic: `accesscity_osmimportstartedevent`
- retry topic: `accesscity_osmimportstartedevent.retry`
- dead-letter topic: `accesscity_osmimportstartedevent.dlq`
- knobs: `Kafka__MaxProcessingAttempts`, `Kafka__RetryDelaySeconds`

Route jobs are persisted to distributed cache for cross-replica polling; poll
`GET /api/v1/routing/jobs/{jobId}` after `POST /api/v1/routing/safe-path/async`.
OSM import jobs are persisted in `osm_import_jobs`; poll
`GET /api/v1/admin/osm/import-jobs/{jobId}` after `POST /import-jobs`.

## Shared Cache

Set `REDIS_CONNECTION=redis:6379` for API and worker containers. HybridCache then uses Redis as
the shared L2 cache for tile, route, and risk lookups instead of per-process memory only.
Risk scoring also uses `IDistributedCache` for external crime/environment data, with IMemoryCache
kept only as a short-lived L1 cache. Imported route graph shards use the same pattern: each worker
keeps a local pre-indexed shard in memory and writes a compact Redis snapshot so newly scaled
workers can hydrate hot graph shards without immediately repeating the PostGIS edge/node load.
OSM import now also precomputes versioned accessibility penalties per route edge for standard,
wheelchair, and stroller profiles. Route workers read those costs from cached graph shards instead
of recomputing surface/smoothness/width/kerb penalties for every A* edge expansion.
Worker config warms several Birmingham shard routes on a timer; add routes that match real demand
clusters before a launch so city-core shards are hot before the first user request.

## Kubernetes Path

Production manifests live in `deploy/kubernetes`:

- `api-deployment.yaml`: stateless API replicas, no workers, no auto migrations
- `worker-deployment.yaml`: Kafka consumers and tile warmer
- `migration-job.yaml`: one-shot schema migration/index job
- `keda-scaledobject.yaml`: API autoscaling from safe-path p95, route saturation, CPU/memory; worker autoscaling from Kafka lag
- `hpa.yaml`: optional CPU/memory fallback for environments that do not run KEDA Prometheus triggers
- `external-secret.example.yaml`: template for External Secrets Operator
- `cnpg-pooler.example.yaml`: optional CloudNativePG PgBouncer pooler for the app runtime path

Create the real `accesscity-api-secrets` Secret from your cloud secret manager before applying the
kustomization. The checked-in `secret.example.yaml` is a template only.

```bash
kubectl apply -f deploy/kubernetes/external-secret.example.yaml
kubectl apply -f deploy/kubernetes/cnpg-pooler.example.yaml
kubectl apply -k deploy/kubernetes
kubectl -n accesscity create job --from=job/accesscity-db-migrate accesscity-db-migrate-manual
```

API and worker pods set `Postgres__AutoMigrate=false` and
`Postgres__AutoSchemaMaintenance=false`; schema work belongs to the migration job to avoid multiple
replicas racing on DDL.
Set `DATABASE_URL` to the PgBouncer/pooler service for API and worker traffic, and set
`DIRECT_DATABASE_URL` to the primary Postgres service. The migration job sets
`Postgres__UseDirectDatabaseUrl=true`, so DDL and schema normalization bypass transaction pooling
while regular traffic stays behind PgBouncer. Keep per-pod `Postgres__MaxPoolSize` small when a
pooler is present; the checked-in manifests use 20 for API pods and 10 for workers.
Set `READONLY_DATABASE_URL` or `READ_REPLICA_DATABASE_URL` when a read replica/pooler is available.
With `Postgres__UseReadOnlyForHotPaths=true`, route graph shard loads use that read-only path and
fall back to the scoped primary connection when no replica URL is configured.
`Postgres__UseStartupSessionParameters` defaults to `false` because providers such as Neon reject
`statement_timeout` startup options on pooled connections; use database-level settings there.

Run the in-cluster k6 job in [DISTRIBUTED_LOAD_TESTING.md](DISTRIBUTED_LOAD_TESTING.md) before increasing traffic.
It exercises the Kubernetes service path against API replicas, workers, Kafka, Redis, and Postgres instead of a
single local process.

## Operational Notes

- Keep `OsmImport__ImportOnStartup=false` on API replicas.
- Mount the same OSM files into workers that receive import jobs.
- Keep Kafka retry/DLQ topics monitored; alert on DLQ message count > 0.
- Run `deploy/postgres/partitioning-readiness.sql` only as a planned production cutover after backups.
- Rotate `Jwt__Key` with `Jwt__PreviousKeys` as described in `docs/SECRET_ROTATION.md`.
- Use the tile profile endpoint, `/api/v1/tiles/{z}/{x}/{y}/profile`, to compare cold vs warm cache latency.
