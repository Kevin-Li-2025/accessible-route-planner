# AccessCity

Accessibility-first routing engine for urban navigation. It uses a combination of OSM road graphs, PostGIS spatial data, and real-time hazard reports to calculate paths based on street safety and physical accessibility.

Project Goal: Support **SDG 11 (Sustainable Cities)** by providing safe transport for persons with disabilities (Target 11.2) and improving public safety via community hazard tracking (Target 11.7).

---

## 🏛 Architecture

AccessCity follows a modular monolithic pattern, utilizing a .NET and React Native stack with dedicated spatial infrastructure.

![Architecture Diagram](docs/images/architecture.png)

---

## 📐 Mathematical Foundation: Cost Function

The routing engine optimizes a multi-objective cost function $C$, balancing accessibility, safety, and travel distance based on the user-selected **Safety Weight** ($\alpha$).

$$ C = (d \cdot (1 - \alpha)) + (\bar{R} \cdot \alpha \cdot w_1) + (P \cdot \alpha \cdot w_2) $$

Where:
- **$d$**: Normalized travel distance (km).
- **$\alpha$**: User safety preference $[0, 1]$.
- **$\bar{R}$**: Cumulative AI Predictive Risk (derived from crime, weather, and time).
- **$P$**: PostGIS Obstacle Penalty (stairs, barriers, surface quality).
- **$w_1, w_2$**: Heuristic importance coefficients (tuned to $5.0$ and $3.0$ respectively).

This function ensures that as $\alpha$ increases, the engine exponentially penalizes inaccessible infrastructure and high-risk zones, triggering detours when the safety benefit outweighs the distance cost.

---

## 🔬 Quantitative Evaluation

We evaluated the routing engine across 10 diverse urban routes in Birmingham, measuring the sensitivity of the **Safety Score** and the resulting **Travel Cost Tradeoff**.

| Route ID | Route Name | Base Dist (km) | Safe Dist (km) | Cost Overhead | **Safety Score** |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **R01** | New St to Bullring | 0.42 | 0.42 | 0.0% | **0.94** |
| **R02** | Library to Town Hall | 0.58 | 0.58 | 0.0% | **0.92** |
| **R03** | **Aston (Hazard Zone)** | 1.40 | 1.85 | **32.1%** | **0.86** |
| **R04** | Digbeth to Southside | 1.83 | 1.83 | 0.0% | **0.91** |
| **R05** | Snow Hill to Colmore Row | 0.67 | 0.67 | 0.0% | **0.93** |
| **R06** | Jewellery Quarter to City | 1.14 | 1.14 | 0.0% | **0.89** |
| **R07** | Five Ways to Brindleyplace | 1.17 | 1.17 | 0.0% | **0.91** |
| **R08** | Edgbaston to Mailbox | 1.96 | 1.96 | 0.0% | **0.90** |
| **R09** | **Curzon (High Risk)** | 1.47 | 3.10 | **110.8%** | **0.84** |
| **R10** | **Grand Central (Const.)** | 1.34 | 1.88 | **40.3%** | **0.87** |

### 📈 Travel Cost Tradeoff Analysis
The evaluation reveals a non-linear relationship between safety and distance. While **70% of routes** require zero distance overhead to maintain high safety (Score > 0.90), the system dynamically identifies "Accessibility Deadzones" (e.g., R09, R10) where it prioritizes safety, adding up to **110% distance** to avoid severe obstacles like construction or unpaved inclines.

---

## 🧪 Ablation Study: Algorithm Sensitivity

To verify the impact of the safety heuristics, we isolated the **Hazard Proximity Factor** and measured the delta in scores for a fixed 200m segment.

| Configuration | Distance (m) | Safety Score | Delta (%) |
| :--- | :--- | :--- | :--- |
| **Control (Direct Path)** | 200m | **0.91** | -- |
| **Treatment 1 (1 Moderate Hazard)** | 200m | **0.72** | **▽ 20.8%** |
| **Treatment 2 (2 Severe Hazards)** | 200m | **0.44** | **▽ 51.6%** |
| **Active Rerouting (Safety-Aware)** | 285m | **0.85** | **△ 93.1% (vs T2)** |

**Finding**: The ablation study confirms that the model is highly sensitive to localized hazards. When safety drops below a critical threshold (e.g., T2), the routing engine overrides the "Shortest Path" heuristic, restoring the safety score via a detour (Active Rerouting).

---

## 🌍 SDG 11 Alignment

Direct technical implementation of UN targets:
- **Target 11.2 (Safe & Accessible Transport)**: Profile-specific routing constraints (Manual vs. Electric Wheelchair) ensuring safe navigation for vulnerable populations.
- **Target 11.7 (Inclusive Public Space)**: Real-time hazard reporting and risk-weighted path-finding to mitigate physical/environmental risks in public areas.

---

## Prerequisites

**.NET SDK 9** — The API, infrastructure projects, and test assembly all target `net9.0`. Install the SDK from the [.NET 9 download page](https://dotnet.microsoft.com/download/dotnet/9.0) and check `dotnet --version` prints 9.x.

**Node.js** — `AccessCity.App` is Expo SDK 54. We standardised on the **Node 20** LTS line; older majors are not something we test against. Before installing JS dependencies, run `node -v` and `npm -v`.

**Docker** — The easiest way to get a matching Postgres/PostGIS instance is the Compose file at the repo root (`postgis/postgis:16-3.4`). Use whichever Docker distribution you already run in the lab (Docker Desktop on macOS/Windows, or Engine + Compose on Linux). Both `docker compose` (plugin) and legacy `docker-compose` are in common use; use whichever your install provides.

**Dependencies** — NuGet packages are declared in the `.csproj` files; `dotnet build`, `dotnet run`, and `dotnet test` restore them as needed. The client’s npm packages live in `AccessCity.App/package.json`. After cloning, run `npm install` inside `AccessCity.App` at least once; the `postinstall` script applies local patches via `patch-package`.

---

## Running the project

Clone from your GitLab remote, then open a terminal at the **repository root** unless a command shows a `cd` into a subfolder.

### Database and API in containers

```bash
docker compose up -d
```

If your machine only has the older CLI, the same thing is `docker-compose up -d`.

That starts Postgres on **localhost:5432** (database `accesscitydb`, user `accesscity`, password `accesscity123` — see `docker-compose.yml`) and builds the `api` service. The HTTP port published to your machine is **8080**, so the base URL is `http://localhost:8080`.

Expect a slow first boot: `appsettings.json` turns on OSM import on startup, and Compose bind-mounts `AccessCity.API/london.osm` and `AccessCity.API/birmingham.osm` into the container. Wait until migrations and graph loading finish before relying on routing or map tiles.

In the Development environment the API also exposes OpenAPI metadata and a Scalar browser UI; the exact paths are printed to the console when Kestrel starts.

### API on the host with `dotnet run`

You still need Postgres reachable from your machine — leaving the `db` service up from the Compose file is enough.

`appsettings.json` ships with `Host=db` in the connection string. That hostname resolves inside the Docker network, **not** from your laptop shell. Point the API at localhost instead for local runs:

```bash
cd AccessCity.API
export ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=accesscitydb;Username=accesscity;Password=accesscity123"
dotnet run
```

On Windows PowerShell:

```powershell
cd AccessCity.API
$env:ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=accesscitydb;Username=accesscity;Password=accesscity123"
dotnet run
```

The default profile in `Properties/launchSettings.json` binds HTTP to **http://localhost:5005** (and HTTPS to 7009).

### Expo client (web, simulator, or device)

```bash
cd AccessCity.App
npm install
npm run web
```

`npm run web` starts the browser bundle. For the interactive dev server menu (QR code, simulators, Expo Go), use `npx expo start` from the same directory after `npm install`.

The client resolves the API base URL in `services/apiConfig.ts` (used from `services/api.ts`): **localhost** on web/iOS simulator and **10.0.2.2** on the Android emulator, defaulting to **port 8080** so it lines up with the containerised API. If you are using `dotnet run` on **5005**, export `EXPO_PUBLIC_API_PORT=5005` (bash) or set the same variable in PowerShell before starting Expo. For a physical handset on Wi‑Fi, set `EXPO_PUBLIC_API_HOST` to your machine’s LAN IP, or set `EXPO_PUBLIC_API_URL` if you need a full origin.

Expo-specific tips (reset scripts, native builds, troubleshooting Metro) stay in [`AccessCity.App/README.md`](AccessCity.App/README.md).

---

## Automated tests

**Backend.** `AccessCity.Tests` is an xUnit project. Most integration tests host the API with `WebApplicationFactory` and call HTTP endpoints (auth, hazards, routing, health, dashboard, geocoding, tiles, offline bundles — see [`docs/TEST_PLAN.md`](docs/TEST_PLAN.md)). There are also **validator-only** tests (`ValidatorUnitTests`) for routing, risk-score, and hazard DTO rules; **load-style** tests (`ApiStressTests`) that fire parallel and staggered bursts while respecting the API’s rate limiter; **spatial stress** tests (`SpatialCachePerformanceTests`, including high read fan-out); plus routing maths, OSM fixtures, map tiles, and benchmark-style route runs (the quantitative tables earlier in this README come from those).

From the repository root:

```bash
dotnet test AccessCity.Tests/AccessCity.Tests.csproj
```

If something fails, rerun with normal verbosity so xUnit prints the failing name and stack:

```bash
dotnet test AccessCity.Tests/AccessCity.Tests.csproj -v n
```

**Client (Jest + Testing Library).** `npm test` in `AccessCity.App` runs `jest-expo` over:

- Pure modules (`services/apiConfig.ts`, `services/hazardMapping.ts`).
- **Screens**: `login`, `signup`, landing `index`, `profile`, `hazard`, web map stub, `reportpage` (with targeted mocks for navigation, location, modals).
- **Components**: `ErrorMessage`, `ThemedText`.

```bash
cd AccessCity.App
npm install
npm test
```

**Client E2E (Playwright, web bundle only).** Chromium drives the same UI you get from `expo start --web` (not the native MapLibre build). Install browsers once, then run the spec (the Playwright config can start Expo for you, or point `PLAYWRIGHT_BASE_URL` at an existing dev server):

```bash
cd AccessCity.App
npx playwright install chromium
npm run test:e2e
```

**GitLab CI.** Pushes and merge requests run **`.gitlab-ci.yml`**: `backend:test` (SDK 9.0, PostGIS + Redis service containers, then `dotnet test AccessCity.Tests`) and `frontend:test` (`npm ci` + `npm test` in `AccessCity.App`). Playwright E2E is local-only to avoid long/flaky browser jobs.

A handful of backend integration cases still call real upstream services (Overpass, Nominatim, police feeds, etc.). The test plan explains where **503**, timeouts, or relaxed assertions are deliberate so the suite stays usable on a laptop with flaky outbound network access.

---

## 🛠 Repository Layout

- `AccessCity.API`: API Layer & Logic.
- `AccessCity.Domain`: Core Entities.
- `AccessCity.Infrastructure`: PostGIS Repositories.
- `AccessCity.App`: Mobile/Web Frontend.
- `AccessCity.Tests`: XUnit & Benchmark Suite.
