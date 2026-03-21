# AccessCity – Test Plan (Milestone 4)

Test plan for the first prototype. Unit tests target single features; integration tests hit the running API. Evidence: test code in repo + this document. Run: `dotnet test AccessCity.Tests/AccessCity.Tests.csproj`.

---

## 1. Scope

| Layer | What we test | How |
|-------|----------------|-----|
| API (backend) | Every HTTP endpoint: status, body shape, validation, auth | Integration tests via `WebApplicationFactory` |
| Services | Caching, hazard fetch, risk scoring, routing logic | Unit tests + integration (through API) |
| Frontend | Screens and flows (login, map, report, profile) | **Jest + React Native Testing Library**: `login`, `signup`, landing `index`, `profile`, `hazard`, `map.web`, `reportpage`, plus `ErrorMessage` / `ThemedText`. **Playwright** (Chromium, Expo web): landing, tab switch, validation, map stub (`npm run test:e2e` in `AccessCity.App`). Native map (`MapScreen.native`) remains manual / device. |

External services (Overpass, OSRM, Nominatim, UK Police) can fail or rate-limit; tests that depend on them allow 503 or skip so the suite stays green.

---

## 2. API Endpoints – Per-Endpoint Coverage

### 2.1 Health

| Endpoint | Method | Tests |
|----------|--------|--------|
| `/health` | GET | 200, response body contains "Healthy" |
| `/health/ready` | GET | 200 (includes DB check when Postgres used) |

No auth. No query/body.

---

### 2.2 Auth (`/api/auth`)

| Endpoint | Method | Success case | Failure / edge cases |
|----------|--------|--------------|------------------------|
| `register` | POST | Valid email + password + fullName → 200, body has token, refreshToken, email, fullName | Duplicate email → 400. Invalid email format → 400 (model validation). Password &lt; 8 chars → 400. Missing body/fields → 400. |
| `login` | POST | Valid email + password → 200, token + refreshToken in body | Wrong password → 401. Unknown email → 401. Empty password → 400. |
| `refresh-token` | POST | Valid refresh token in query → 200, new token pair | Invalid/expired token → 401. Missing query param → 400. |
| `revoke-token` | POST | Valid token in query → 200 | Invalid token → 404. Already revoked → 400. |
| `forgot-password` | POST | Any email → 200 (same message for known/unknown to avoid enumeration) | Invalid email format → 400. |
| `reset-password` | POST | Valid email + valid token + new password → 200 | Wrong/expired token → 400. Unknown email → 400. New password &lt; 8 chars → 400. |

All request/response bodies JSON. Auth endpoints are rate-limited (fixed window); tests don’t rely on exact rate limit behaviour.

---

### 2.3 Hazards (`/api/hazards`)

| Endpoint | Method | Success case | Failure / edge cases |
|----------|--------|--------------|------------------------|
| (list) | GET | No query → 200, JSON array (default bbox Birmingham). With minLat, minLng, maxLat, maxLng → 200, array | minLat &gt; maxLat or minLng &gt; maxLng → 400. Lat outside [-90,90] or lng outside [-180,180] → 400. |
| (by id) | GET | Existing hazard id → 200, body has id, location, type, description, status, reportedAt | Non-existent guid → 404. Invalid guid format → 400. |
| (report) | POST | Body: location (GeoJSON Point), type, description (photoUrl optional) → 201, Location header, body has id and status 0 (Reported) | Missing location → 400. Missing type or empty string → 400. Missing description or empty → 400. Coordinates outside WGS84 → 400. Type/description trimmed and length-capped server-side. |
| (update status) | PATCH | Existing id + body = status enum value (0–3) → 204 | Non-existent id → 404. Invalid status value → 400. |

Location: GeoJSON `{ "type": "Point", "coordinates": [ lon, lat ] }`. Status: 0 Reported, 1 UnderReview, 2 Resolved, 3 Dismissed.

---

### 2.4 Routing (`/api/routing`)

| Endpoint | Method | Success case | Failure / edge cases |
|----------|--------|--------------|------------------------|
| `safe-path` | POST | Body: start/end coordinates (x=lng, y=lat), safetyWeight 0–1 → 200, body has path (or fallback), distance, estimatedTime, safetyScore, steps, warnings | Missing start or end → 400. Invalid coords (outside WGS84) → 400. safetyWeight &lt; 0 or &gt; 1 → 400. |
| `risk-score` | GET | Query: lat, lng, optional radius (default 500, max 5000) → 200, overallRisk and breakdown | Lat/lng out of range → 400. radius ≤ 0 or &gt; 5000 → 400. |
| `ai-risk-score` | GET | Query: lat, lng, optional radius → 200, overall risk + factors | Invalid coords → 400. |

Coordinates in degrees; radius in metres.

---

### 2.5 Dashboard (`/api/dashboard`)

| Endpoint | Method | Success case | Failure / edge cases |
|----------|--------|--------------|------------------------|
| `summary` | GET | 200, body: totalHazards, activeUsers, pendingAlerts, resolved | Can return 503 if hazard backend (Overpass) fails. |
| `heat-map` | GET | 200, GeoJSON FeatureCollection (features array) | 503 if hazard backend fails. |
| `infrastructure-feed` | GET | Query: optional limit (1–100) → 200, array of hazard items, length ≤ limit | limit &gt; 100 → clamped to 100. |

No auth. All read-only.

---

### 2.6 Geocoding (`/api/geocoding`)

| Endpoint | Method | Success case | Failure / edge cases |
|----------|--------|--------------|------------------------|
| `search` | GET | Query: query (non-empty) → 200, array of results (or 503 if Nominatim rate limit) | Empty query → 400. |
| `reverse` | GET | Query: lat, lon (WGS84) → 200, single result (or 503) | Lat not in [-90,90] or lon not in [-180,180] → 400. |

---

### 2.7 Spatial (`/api/spatial`)

| Endpoint | Method | Success case | Failure / edge cases |
|----------|--------|--------------|------------------------|
| `poi` | GET | Query: lat, lng, optional radius → 200, array (currently empty) | - |
| `map-overlay` | GET | Query: layerName → 200, FeatureCollection with features array | - |

Stubs; we only check 200 and response shape.

---

### 2.8 Offline map (`/api/offlinemap`) – requires auth

| Endpoint | Method | Success case | Failure / edge cases |
|----------|--------|--------------|------------------------|
| `bundle` | GET | Query: minLat, minLng, maxLat, maxLng, valid Bearer token → 200, body has area, hazards, timestamp, version | No token or invalid → 401. minLat &gt; maxLat or minLng &gt; maxLng → 400. Invalid lat/lng range → 400. 503 if hazard service fails. |

---

### 2.9 Map tiles (`/api/tiles`) – requires auth

| Endpoint | Method | Success case | Failure / edge cases |
|----------|--------|--------------|------------------------|
| `{z}/{x}/{y}.pbf` | GET | Valid z,x,y and Bearer token → 200 (vector tile bytes) or 204 (no data) | No token or invalid → 401. |

---

## 3. Unit-Level / Component Coverage

### 3.1 Auth

- **Register**: email uniqueness, password hashing (Argon2), refresh token creation, token in response.
- **Login**: password check, refresh token rotation (old ones revoked).
- **Refresh token**: lookup by token, active check, new token pair, old token revoked.
- **Token service**: JWT creation (claims, expiry), refresh token generation (length, expiry).
- **Rate limiting**: auth policy applied to auth controller; exact limit not asserted in tests.

### 3.2 Hazards

- **List**: uses `IRealHazardDataService.GetActiveHazardsAsync` with optional bbox; bbox validation in service (throws → 400 via filter).
- **Report**: validation (location, type, description), persistence to DB, server-set id/reportedAt/status.
- **Get by id**: read from DB; 404 when not found.
- **Patch**: update status in DB; 404 when not found.

### 3.3 Routing

- **Safe-path**: OSRM call, hazard proximity, safety weight, response shape (path, distance, time, score, steps, warnings). Fallback when OSRM unavailable.
- **Risk-score**: hazard density, infrastructure, UK Police data (cached), response shape.
- **AI risk-score**: multi-factor model, response shape and factor breakdown.

### 3.4 Dashboard

- **Summary / heat-map / feed**: all use `GetActiveHazardsAsync`; can 503 when Overpass times out or rate-limits.

### 3.5 Geocoding

- **Search / reverse**: named HttpClient (Nominatim), timeout, coord validation on reverse.

### 3.6 Caching / infra

- **Spatial cache**: quadtree + HybridCache; insert and query by bounds (unit tests in SpatialCacheTests).
- **Bloom filter**: used for tile lookup optimisation (MapTileTests).
- **RealHazardDataService**: in-memory cache keyed by bbox; Overpass + DB merge; bbox validation.

### 3.7 Exception handling

- **OverpassExceptionFilter**: maps `OverpassServiceException` → 503 with correlation id.
- **BadRequestExceptionFilter**: maps `ArgumentException` → 400 with message (e.g. bbox validation).

### 3.8 FluentValidation (unit, no HTTP)

- **RouteRequestValidator**, **RiskScoreRequestValidator**, **CreateHazardRequestValidator**: valid/invalid coordinates, profiles, safety weight, radius bounds, string lengths — covered in `ValidatorUnitTests.cs`.

### 3.9 Client TypeScript (Jest)

- **`apiConfig.resolveApiUrls`**: env-based base URL / Android host / port overrides — `AccessCity.App/__tests__/apiConfig.test.ts`.
- **Hazard mapping**: status labels, type labels, GeoJSON → app model — `AccessCity.App/__tests__/hazardMapping.test.ts`.

### 3.10 Client screens & components (Jest + `@testing-library/react-native`)

- **`__tests__/screens/*.screen.test.tsx`**: `login`, `signup`, `index` (landing auth), `profile` (with `AuthContext` test wrapper), `hazard` (mocked `hazardsService` + `useFocusEffect`), `map.web`, `reportpage` (mocked modal).
- **`__tests__/components/*`**: `ErrorMessage`, `ThemedText`.

### 3.11 E2E (Playwright, web only)

- **`AccessCity.App/e2e/app.spec.ts`**: opens Expo web bundle — branding, Sign Up tab + Full Name field, empty submit validation (`testID="index-auth-submit"`), `/map` stub copy.
- First run: `npx playwright install chromium` (from `AccessCity.App` or repo root with `npx playwright install` using local devDependency). Config can start `expo start --web` automatically, or set `PLAYWRIGHT_SKIP_WEBSERVER=1` if you already have a dev server on `PLAYWRIGHT_BASE_URL`.

### 3.12 Load and spatial stress (backend)

- **`ApiStressTests`**: parallel and staggered `/health`, parallel spatial POI, parallel hazards list (OK or 503), parallel authenticated dashboard feed (OK or 503). Sized to avoid tripping the global sliding rate limiter in Development.
- **`SpatialCachePerformanceTests`**: concurrent hazard writes + spatial queries; additional high read fan-out over a hot bounding box.

---

## 4. Frontend Components (manual / future E2E)

| Component / screen | What to check manually |
|--------------------|------------------------|
| Login | Valid credentials → token stored, redirect. Invalid → error shown. |
| Sign up | Valid data → 200, then can log in. Duplicate email → error. |
| Forgot / reset password | Request token → message. Reset with token → success; then login with new password. |
| Map | Loads; shows hazards if backend returns them; can select destination. |
| Report hazard | Open modal, set location/type/description, submit → 201; appears in list or map when refetched. |
| Hazard list / detail | List loads; tap item → detail; status if we add UI for it. |
| Profile | Shows user info; logout clears token. |
| Routing / safety | Request route → path and safety info shown; risk score at point. |
| Offline bundle | Authenticated request with bbox → bundle with hazards (or 503). |

Automated **web E2E** (Playwright) covers the landing and map-web flows; full **native** navigation and MapLibre remain manual or future Detox/Maestro if the team adds them.

---

## 5. Test Data and Environment

- **API tests**: `WebApplicationFactory` against **real PostgreSQL/PostGIS** (and Redis for the API stack), via `AccessCityApiFactory` + `DATABASE_URL` / `Postgres:ConnectionString`. Auth uses real JWT and Argon2. **GitLab CI** provisions PostGIS + Redis and runs `dotnet test` (see `.gitlab-ci.yml`).
- **Hazard POST**: In-memory provider may not persist geometry; tests accept 201 (when Postgres used) or 500 (in-memory) for POST; GET by id and PATCH are tested when POST succeeds.
- **External services**: Overpass, OSRM, Nominatim, UK Police – live calls in integration tests; 503 or timeouts are allowed where documented so the suite doesn’t fail on network issues.

---

## 6. Evidence Checklist

- [x] This TEST_PLAN.md in repo.
- [x] Backend unit/integration/stress: AuthTests, RoutingTests, MapTileTests, SpatialCacheTests, SpatialCachePerformanceTests, ApiIntegrationTests, DeepApiTests, ValidatorUnitTests, ApiStressTests, BenchmarkTests, OsmImportTests, etc.
- [x] Client unit + component/screen tests: `AccessCity.App/__tests__/**/*.test.ts(x)`.
- [x] `dotnet test AccessCity.Tests/AccessCity.Tests.csproj` passes (or only known flaky 503s from upstream services).
- [x] `npm test` under `AccessCity.App` passes.
- [x] Playwright E2E spec present (`e2e/app.spec.ts`); run `npx playwright install chromium` once, then `npm run test:e2e`.
- [x] README states how to run tests (backend + client + E2E).
- [x] GitLab CI runs `backend:test` and `frontend:test` (see `.gitlab-ci.yml`).

---

## 7. Run Commands

```bash
# From repo root — backend (xUnit)
dotnet test AccessCity.Tests/AccessCity.Tests.csproj

# Verbose
dotnet test AccessCity.Tests/AccessCity.Tests.csproj -v n
```

```bash
# Expo app — Jest (modules + screens + components)
cd AccessCity.App
npm install
npm test
```

```bash
# One-time browser binaries for E2E
cd AccessCity.App
npx playwright install chromium

# E2E against Expo web (starts dev server from playwright.config unless SKIP is set)
npm run test:e2e
```

Backend tests live in one project; the client uses Jest + `jest-expo` and Playwright separately.

---

## 8. GitLab CI

Pipelines (stage `test`):

| Job | What runs |
|-----|-----------|
| `backend:test` | `dotnet restore` / `build` / `test` on `CodeConquerors.sln` → `AccessCity.Tests`. Services: **postgis/postgis:16-3.4** (`postgres` host), **redis:7-alpine** (`redis` host). Env: `DATABASE_URL`, `ConnectionStrings__Redis`. Timeout 45 min (integration suite is slow). |
| `frontend:test` | `cd AccessCity.App && npm ci && npm test -- --ci`. Cached `node_modules` from `package-lock.json`. |

**Playwright** is not executed in CI (would require `expo start --web` + browser install; run locally with `npm run test:e2e`).
