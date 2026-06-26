# AccessCity

AccessCity is an accessibility-focused urban routing system. It combines a .NET 9 geospatial API, PostGIS-backed OSM ingestion, Redis/Kafka worker paths, route graph preprocessing, hazard/risk scoring, vector tile support, and an Expo React Native client.

The project is built for safe, reproducible route decisions: AI assistance is limited to text normalization, report summarization, and explanations. It does not generate routes or dynamically change routing edge costs.

## Status

AccessCity is an active pre-production system. The local and Kubernetes deployment paths are wired for API replicas, background workers, Kafka, Redis, PostGIS/PgBouncer, observability, SLO alerts, and route graph artifact profiling, but it is not an emergency service and has not yet been proven at global production scale.

Current verified baseline:

- Distributed k6 run: 6 API pods, 2 worker pods, 3 Postgres instances, 3 PgBouncer pods, 3 Kafka brokers, Redis; 145,334 requests, 440 req/s, 0 HTTP failures, safe-path p95 167.56 ms in the checked-in scenario.
- Real city API p99 matrix: West Midlands OSM extract, 754,727 route nodes, 1,633,260 route edges; 4/16/64/128 rps local-graph runs with 0 HTTP failures. At 128 rps, 5,759 requests completed with p95 6.51 ms and p99 239.90 ms.
- Low-latency spatial kernels: 1,000,000 hazards and 10,000,000 queries. C++ dense-grid lookup reaches 185.4M ops/s single-thread p99 0.0194 us, and 2.1B ops/s on 10 threads. The .NET H3 hot path reaches 137.3M ops/s with p99 0.027 us.
- Full backend test suite: 307 xUnit tests passing in Release mode on the current branch.

See [Production Performance Playbook](docs/PRODUCTION_PERFORMANCE_PLAYBOOK.md), [K6 load results](docs/K6_LOAD_TEST_RESULTS.md), [route graph preprocessing](docs/ROUTE_GRAPH_PREPROCESSING.md), and [distributed load testing](docs/DISTRIBUTED_LOAD_TESTING.md) for methodology and limits.
Accessibility planning intelligence is documented in [Accessibility Planning Intelligence](docs/ACCESSIBILITY_PLANNING_INTELLIGENCE.md).

## What It Does

- Accessibility-aware routing for standard, manual wheelchair, power wheelchair, and stroller profiles.
- OSM import into a routable pedestrian graph with accessibility tags such as surface, width, incline, kerb, and crossing metadata where available.
- Safe-path scoring using deterministic route costs, nearby hazards, infrastructure signals, lighting/environmental inputs, and profile-specific penalties.
- Hazard reporting, moderation-oriented admin workflows, and live hazard alert broadcasting.
- Map overlays, POI queries, vector tile endpoints, and offline map bundle support.
- Route graph preprocessing with packed artifacts, source shard manifests, ALT landmarks, optional contraction hierarchy support, distributed cache coalescing, and profile quality gates.

## Architecture

![AccessCity architecture](docs/images/architecture.png)

AccessCity is a modular monolith with independently scalable deployment roles:

- API: authentication, HTTP endpoints, WebSocket hazard alerts, cache lookups, job submission, and fast read paths.
- Workers: route jobs, OSM import jobs, tile warming, graph artifact warmup, and CPU-heavy background work.
- PostGIS: source of truth for users, hazards, OSM graph data, imports, and spatial read models.
- Redis: distributed cache for route results, risk summaries, tile data, job status, and packed graph artifacts.
- Kafka: route/import job handoff and multi-replica worker coordination.
- Kubernetes: KEDA-driven API/worker scaling, pod disruption budgets, topology spread, probes, and separate migration jobs.

Module boundaries are documented in [Modular Architecture](docs/MODULAR_ARCHITECTURE.md). Scaling guardrails are documented in [SLO and Scaling](docs/SLO_AND_SCALING.md).

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Node.js 20 LTS
- Docker Desktop or Docker Engine with Compose
- Optional: `kubectl`, `kustomize`, and a Kubernetes cluster for distributed validation

## Quick Start

Run the API, PostGIS, Redis, Kafka, and the routing/import worker:

```bash
docker compose --profile worker up -d --build
```

The API is exposed through the local gateway at:

```text
http://localhost:8080
```

Useful local endpoints:

- `GET /health`
- `GET /health/ready`
- Development OpenAPI: `http://localhost:8080/openapi/v1.json`
- Development Scalar UI: `http://localhost:8080/scalar/v1`

Stop the stack:

```bash
docker compose --profile worker down
```

## Run the API on the Host

Keep only the backing services in Docker, then run the API locally:

```bash
docker compose up -d db redis kafka

cd AccessCity.API
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=accesscitydb;Username=accesscity;Password=accesscity123"
export ConnectionStrings__Redis="localhost:6379"
export Messaging__UseKafka=false
export Routing__DispatchJobsToWorker=false
dotnet run
```

The default development launch settings expose HTTP on port 5005 unless overridden.

## Run the Expo Client

```bash
cd AccessCity.App
npm ci
npm run web
```

For simulator, device, and QR workflows:

```bash
npx expo start
```

The client API base URL is resolved in `AccessCity.App/services/apiConfig.ts`. Override it with `EXPO_PUBLIC_API_URL`, `EXPO_PUBLIC_API_HOST`, or `EXPO_PUBLIC_API_PORT`.

## Configuration

Most runtime settings use ASP.NET configuration keys and can be supplied through environment variables with `__` separators.

High-value settings:

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings__DefaultConnection` | PostGIS primary connection string. |
| `READONLY_DATABASE_URL` | Optional read-only Postgres/PgBouncer path for hot geospatial reads. |
| `ConnectionStrings__Redis` | Redis L2 cache and distributed job status path. |
| `Messaging__UseKafka` | Enables Kafka-backed job publishing and worker consumption. |
| `Kafka__BootstrapServers` | Kafka broker list. |
| `Jwt__Key` / `Jwt__PreviousKeys` | Current and previous JWT signing keys for rotation. |
| `Routing__AsyncFirstForCacheMiss` | Returns `202 Accepted` for cold route misses instead of blocking API pods. |
| `Routing__DispatchJobsToWorker` | Sends route jobs to workers rather than computing in the API process. |
| `Routing__RouteGraphPackedArtifactsEnabled` | Uses compact binary graph artifacts for cache/storage. |
| `Routing__RouteGraphMaxDistributedSnapshotBytes` | Prevents oversized merged route graph bundles from being written to Redis/L2. |
| `Routing__RouteGraphCorridorSlicingEnabled` / `Routing__RouteGraphCorridorPaddingMetres` | Loads a route corridor of reusable graph cells instead of the whole padded rectangle for city-scale routes. |
| `Routing__RouteGraphAdaptiveCorridorWideningEnabled` | Retries a wider route graph corridor when the first slice lacks endpoint coverage or a connected accessible path. |
| `Routing__RouteGraphProfileFailOnQualityGate` | Fails graph profiling when configured artifact/latency budgets are exceeded. |

Secret rotation is documented in [Secret Rotation](docs/SECRET_ROTATION.md). Do not use the checked-in development JWT placeholder in production.

## Testing and Quality Gates

Backend format, build, and tests:

```bash
dotnet format CodeConquerors.sln --verify-no-changes --verbosity minimal
dotnet build CodeConquerors.sln --configuration Release
dotnet test AccessCity.Tests/AccessCity.Tests.csproj --configuration Release --no-build --verbosity normal
```

Frontend lint and tests:

```bash
cd AccessCity.App
npm ci
npm run lint
npm run test:ci
```

Dependency vulnerability checks:

```bash
dotnet list CodeConquerors.sln package --vulnerable --include-transitive

cd AccessCity.App
npm audit --audit-level=high --registry=https://registry.npmjs.org
```

Container and Kubernetes config checks:

```bash
docker compose config --quiet
docker compose --profile worker config --quiet
kubectl kustomize deploy/kubernetes >/tmp/accesscity-kustomize.yaml
```

CI gates are documented in [CI/CD](docs/CI_CD.md). Test architecture is documented in [Test Architecture](docs/TEST_ARCHITECTURE.md).

## Route Graph Profiling

Profile a real city OSM extract through the offline graph preprocessing path:

```bash
tools/profile-city-route-graph.sh
```

The script downloads a Birmingham extract by default, builds source shards, packs graph artifacts, profiles warmup routes, and writes:

```text
data/route-graph-artifacts/profile-report.json
```

Use fail mode in release validation:

```bash
Routing__RouteGraphProfileFailOnQualityGate=true \
Routing__RouteGraphProfileMaxRedisPayloadBytes=8388608 \
Routing__RouteGraphProfileMaxArtifactUnpackMilliseconds=150 \
tools/profile-city-route-graph.sh
```

The profile report is intentionally strict. A failed quality gate does not mean the app is broken; it means a city graph or route bundle exceeds the configured production budget and should be split, cached differently, or preprocessed more aggressively before rollout.

## Kubernetes Deployment

Render manifests:

```bash
kubectl kustomize deploy/kubernetes
```

Apply the base stack:

```bash
kubectl apply -k deploy/kubernetes
```

The Kubernetes manifests include:

- API and worker deployments
- migration job
- service and ingress
- KEDA scaled objects
- pod disruption budgets
- topology spread constraints
- secret examples and ExternalSecret examples
- k6 distributed load and soak test jobs

Before production, replace example secrets, configure a managed Postgres/PostGIS or CloudNativePG path, point `READONLY_DATABASE_URL` at a read pooler/replica, and run the distributed k6 job in [Distributed Load Testing](docs/DISTRIBUTED_LOAD_TESTING.md).

## Observability

The local observability stack is under `deploy/observability`:

- OpenTelemetry Collector
- Prometheus
- Grafana provisioning
- API performance alerts
- SLO burn-rate alerts

SLOs include safe-path p95, API 5xx rate, route computation saturation, external dependency fallback rate, and shared cache hit ratio. See [SLO and Scaling](docs/SLO_AND_SCALING.md).

## Security Notes

- Production JWT keys must come from a secret manager or Kubernetes Secret/ExternalSecret.
- JWT key rotation supports `Jwt__PreviousKeys`.
- External APIs are guarded by timeout, bulkhead, circuit-breaker, and fallback paths.
- Public Overpass, OSRM, Police, weather, and environmental APIs should not sit on hot production request paths; use background enrichment and cached signals.
- Security reporting and deployment hardening expectations are documented in [Security](SECURITY.md).

## Known Limits

- The verified load tests prove the checked-in distributed path, not unlimited linear scale.
- Long cross-city route bundles can exceed the Redis/L2 payload budget; these are now flagged and kept out of distributed cache.
- City-scale routing still needs stronger graph preprocessing for the next tier: route-level slicing, boundary overlays, CCH/CRP-style customization, and persistent versioned graph releases.
- Accessibility quality is bounded by OSM/source data completeness. Missing width, kerb, surface, incline, curb ramp, and obstruction tags reduce route confidence.
- AccessCity remains pre-production and should not be represented as an emergency service or globally proven production system.

## Project Layout

```text
AccessCity.API/             .NET 9 API, modules, workers, routing, geospatial services
AccessCity.App/             Expo React Native client
AccessCity.Tests/           xUnit integration, routing, architecture, stress, and benchmark tests
AccessCity.SoakTestRunner/  standalone soak/allocation harness
deploy/kubernetes/          Kubernetes production manifests and load/soak jobs
deploy/observability/       OpenTelemetry, Prometheus, Grafana, and alerts
docs/                       architecture, operations, scaling, security, and test reports
tools/                      city route graph profiling tooling
data/                       local OSM extracts and generated graph artifacts
```

## Documentation Index

- [CI/CD](docs/CI_CD.md)
- [Accessibility Vision Evaluation](docs/ACCESSIBILITY_VISION_EVALUATION.md)
- [Distributed Load Testing](docs/DISTRIBUTED_LOAD_TESTING.md)
- [Geospatial Query Audit](docs/GEOSPATIAL_QUERY_AUDIT.md)
- [K6 Load Test Results](docs/K6_LOAD_TEST_RESULTS.md)
- [Modular Architecture](docs/MODULAR_ARCHITECTURE.md)
- [Multi-Instance Deployment](docs/MULTI_INSTANCE_DEPLOYMENT.md)
- [Route Graph Preprocessing](docs/ROUTE_GRAPH_PREPROCESSING.md)
- [Scalability and Modularity Report](docs/SCALABILITY_AND_MODULARITY_REPORT.md)
- [Secret Rotation](docs/SECRET_ROTATION.md)
- [SLO and Scaling](docs/SLO_AND_SCALING.md)
- [Soak and Chaos Testing](docs/SOAK_AND_CHAOS_TESTING.md)
- [Stress Test Report](docs/STRESS_TEST_REPORT.md)
- [Test Architecture](docs/TEST_ARCHITECTURE.md)
- [Three-Minute Demo Walkthrough](docs/demo/DEMO.md)

## Contributing

Open an issue or pull request with a clear problem statement, reproduction steps, and the validation commands you ran. For routing changes, include profile-specific route quality tests or benchmark fixtures whenever behavior changes.

See [Contributing](CONTRIBUTING.md) and [Code of Conduct](CODE_OF_CONDUCT.md).

## License

AccessCity is licensed under the [MIT License](LICENSE).
