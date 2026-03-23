# AccessCity

Accessibility-focused urban routing: .NET 9 API (PostGIS, OSM, hazards) and an Expo (`AccessCity.App`) client.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Node.js 20 LTS (for the app)
- Docker (recommended: Postgres + Redis + API via Compose)

## Run with Docker

From the repo root:

```bash
docker compose up -d
```

API: `http://localhost:8080`. Postgres: `localhost:5432` (see `docker-compose.yml` for database name and credentials).

Optional Redis: set `ConnectionStrings__Redis` / `REDIS_CONNECTION` (e.g. `redis:6379` in Compose) so HybridCache uses Redis as L2; otherwise the API uses in-memory L2.

## API on the host

Keep the `db` (and optionally `redis`) services up, then point the API at localhost:

```bash
cd AccessCity.API
export ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=accesscitydb;Username=accesscity;Password=accesscity123"
dotnet run
```

Default dev URLs are in `Properties/launchSettings.json` (e.g. HTTP on 5005).

## Expo client

```bash
cd AccessCity.App
npm install
npm run web
```

For the full dev menu (simulator / device / QR), use `npx expo start`. API base URL is resolved in `services/apiConfig.ts`; override with `EXPO_PUBLIC_API_PORT`, `EXPO_PUBLIC_API_HOST`, or `EXPO_PUBLIC_API_URL` if needed. Optional Sentry: see `AccessCity.App/.env.example`.

## Tests

```bash
dotnet test AccessCity.Tests/AccessCity.Tests.csproj
```

```bash
cd AccessCity.App
npm test
```

## Scripts (API must be running)

```bash
python3 scripts/probe_api_endpoints.py http://127.0.0.1:8080
python3 scripts/measure_api_latency.py http://127.0.0.1:8080 30 3
```

## Layout

- `AccessCity.API` — HTTP API
- `AccessCity.App` — Expo client
- `AccessCity.Tests` — xUnit integration and benchmarks
- `deploy/observability` — optional Compose stack (OTLP → Prometheus → Grafana) for local metrics
- `docs/images/architecture.png` — architecture diagram
