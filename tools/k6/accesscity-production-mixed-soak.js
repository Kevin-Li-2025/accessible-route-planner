import http from 'k6/http';
import { check, sleep } from 'k6';
import exec from 'k6/execution';
import { Rate, Trend } from 'k6/metrics';

const baseUrl = __ENV.BASE_URL || 'http://127.0.0.1:5099';
const duration = __ENV.DURATION || '30m';
const routeDatasetFile = __ENV.ROUTE_DATASET_FILE || 'tools/k6/birmingham-city-routes.json';
const routePollTimeoutSeconds = Number(__ENV.ROUTE_POLL_TIMEOUT_SECONDS || 10);

const routeRate = Number(__ENV.ROUTE_RATE || 20);
const riskRate = Number(__ENV.RISK_RATE || 120);
const poiRate = Number(__ENV.POI_RATE || 60);
const hazardRate = Number(__ENV.HAZARD_RATE || 60);
const dashboardRate = Number(__ENV.DASHBOARD_RATE || 20);
const accountRate = Number(__ENV.ACCOUNT_RATE || 10);
const tileRate = Number(__ENV.TILE_RATE || 0);
const readinessRate = Number(__ENV.READINESS_RATE || 10);
const overallP95Ms = Number(__ENV.SLO_OVERALL_P95_MS || 1200);
const overallP99Ms = Number(__ENV.SLO_OVERALL_P99_MS || 3000);
const routeP95Ms = Number(__ENV.SLO_ROUTE_P95_MS || 500);
const routeP99Ms = Number(__ENV.SLO_ROUTE_P99_MS || 2000);
const hotReadP95Ms = Number(__ENV.SLO_HOT_READ_P95_MS || 350);
const hotReadP99Ms = Number(__ENV.SLO_HOT_READ_P99_MS || 1200);
const riskP95Ms = Number(__ENV.SLO_RISK_P95_MS || 250);
const riskP99Ms = Number(__ENV.SLO_RISK_P99_MS || 1000);
const dashboardP95Ms = Number(__ENV.SLO_DASHBOARD_P95_MS || 500);
const dashboardP99Ms = Number(__ENV.SLO_DASHBOARD_P99_MS || 1500);
const checkRate = Number(__ENV.SLO_CHECK_RATE || 0.97);
const httpFailureRate = Number(__ENV.SLO_HTTP_FAILURE_RATE || 0.02);
const productionFailureRate = Number(__ENV.SLO_PRODUCTION_FAILURE_RATE || 0.02);
const routeTimeoutRate = Number(__ENV.SLO_ROUTE_TIMEOUT_RATE || 0.02);

export const productionApiFailure = new Rate('production_api_failure');
export const routeJobTimeout = new Rate('route_job_timeout');
export const routeEndToEndMs = new Trend('route_end_to_end_ms', true);

http.setResponseCallback(http.expectedStatuses(
  { min: 200, max: 399 },
  400,
  404,
  429,
  503,
  504,
));

function scenario(rate, preAllocatedVUs, maxVUs, execName) {
  return {
    executor: 'constant-arrival-rate',
    rate,
    timeUnit: '1s',
    duration,
    preAllocatedVUs,
    maxVUs,
    exec: execName,
    gracefulStop: '45s',
  };
}

const scenarios = {};
if (routeRate > 0) scenarios.routing = scenario(routeRate, 80, 800, 'routing');
if (riskRate > 0) scenarios.risk = scenario(riskRate, 80, 800, 'risk');
if (poiRate > 0) scenarios.poi = scenario(poiRate, 50, 500, 'poi');
if (hazardRate > 0) scenarios.hazards = scenario(hazardRate, 50, 500, 'hazards');
if (dashboardRate > 0) scenarios.dashboard = scenario(dashboardRate, 25, 250, 'dashboard');
if (accountRate > 0) scenarios.account = scenario(accountRate, 20, 200, 'account');
if (tileRate > 0) scenarios.tiles = scenario(tileRate, 20, 200, 'tiles');
if (readinessRate > 0) scenarios.readiness = scenario(readinessRate, 10, 100, 'readiness');

export const options = {
  scenarios,
  thresholds: {
    http_req_failed: [`rate<${httpFailureRate}`],
    checks: [`rate>${checkRate}`],
    production_api_failure: [`rate<${productionFailureRate}`],
    route_job_timeout: [`rate<${routeTimeoutRate}`],
    http_req_duration: [`p(95)<${overallP95Ms}`, `p(99)<${overallP99Ms}`],
    'http_req_duration{name:route-submit}': [`p(95)<${routeP95Ms}`, `p(99)<${routeP99Ms}`],
    'http_req_duration{name:risk-score}': [`p(95)<${riskP95Ms}`, `p(99)<${riskP99Ms}`],
    'http_req_duration{name:poi}': [`p(95)<${hotReadP95Ms}`, `p(99)<${hotReadP99Ms}`],
    'http_req_duration{name:hazards-page}': [`p(95)<${hotReadP95Ms}`, `p(99)<${hotReadP99Ms}`],
    'http_req_duration{name:dashboard-summary}': [`p(95)<${dashboardP95Ms}`, `p(99)<${dashboardP99Ms}`],
  },
  summaryTrendStats: ['min', 'avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

const jsonHeaders = { headers: { 'Content-Type': 'application/json' }, timeout: '10s' };
const routePairs = JSON.parse(open(routeDatasetFile)).routes;

function ok(response, allowed = [200]) {
  const passed = allowed.includes(response.status);
  productionApiFailure.add(!passed);
  return passed;
}

function parseJson(response) {
  try { return response.json(); } catch (_) { return null; }
}

function offset(width = 1009, step = 0.00001) {
  return ((exec.scenario.iterationInTest % width) - Math.floor(width / 2)) * step;
}

function routeAt() {
  return routePairs[exec.scenario.iterationInTest % routePairs.length];
}

export function setup() {
  const email = `soak-${Date.now()}-${Math.floor(Math.random() * 100000)}@accesscity.local`;
  const register = http.post(`${baseUrl}/api/v1/auth/register`, JSON.stringify({
    email,
    password: 'P@ssword123!',
    fullName: 'Production Soak User',
  }), jsonHeaders);
  return { token: register.status >= 200 && register.status < 300 ? register.json('token') || '' : '' };
}

export function routing() {
  const started = Date.now();
  const route = routeAt();
  const body = JSON.stringify({
    start: route.start,
    end: route.end,
    profile: route.profile || 'standard',
    safetyWeight: route.safetyWeight ?? 0.5,
    preferences: route.preferences || [],
  });
  const response = http.post(`${baseUrl}/api/v1/routing/safe-path/options`, body, {
    ...jsonHeaders,
    tags: { name: 'route-submit' },
  });
  check(response, { 'route accepted': (r) => ok(r, [200, 202, 404]) });
  if (response.status !== 202) {
    routeEndToEndMs.add(Date.now() - started);
    return;
  }
  const payload = parseJson(response);
  if (!payload?.jobId) {
    routeJobTimeout.add(true);
    return;
  }
  const deadline = Date.now() + routePollTimeoutSeconds * 1000;
  while (Date.now() < deadline) {
    const poll = http.get(`${baseUrl}/api/v1/routing/jobs/${encodeURIComponent(payload.jobId)}`, {
      timeout: '3s',
      tags: { name: 'route-poll' },
    });
    const json = parseJson(poll);
    if (poll.status === 200 && (json?.status === 'Completed' || json?.status === 2)) {
      routeJobTimeout.add(false);
      routeEndToEndMs.add(Date.now() - started);
      return;
    }
    if (poll.status >= 500 || poll.status === 429 || json?.status === 'Failed' || json?.status === 3) {
      routeJobTimeout.add(true);
      return;
    }
    sleep(0.2);
  }
  routeJobTimeout.add(true);
}

export function risk() {
  const route = routeAt();
  const d = offset();
  const lat = route.start.y + d;
  const lng = route.start.x - d;
  check(http.get(`${baseUrl}/api/v1/routing/risk-score?lat=${lat}&lng=${lng}&radius=500`, {
    timeout: '4s',
    tags: { name: 'risk-score' },
  }), { 'risk ok': (r) => ok(r, [200]) });
}

export function poi() {
  const route = routeAt();
  check(http.get(`${baseUrl}/api/v1/spatial/poi?lat=${route.start.y}&lng=${route.start.x}&radius=800`, {
    timeout: '4s',
    tags: { name: 'poi' },
  }), { 'poi ok': (r) => ok(r, [200]) });
}

export function hazards() {
  check(http.get(`${baseUrl}/api/v1/hazards/page?status=Reported&limit=50`, {
    timeout: '4s',
    tags: { name: 'hazards-page' },
  }), { 'hazards ok': (r) => ok(r, [200]) });
}

export function dashboard() {
  check(http.get(`${baseUrl}/api/v1/dashboard/summary`, {
    timeout: '4s',
    tags: { name: 'dashboard-summary' },
  }), { 'dashboard ok': (r) => ok(r, [200]) });
}

export function account(data) {
  if (!data.token) return;
  check(http.get(`${baseUrl}/api/v1/account/profile`, {
    headers: { Authorization: `Bearer ${data.token}` },
    timeout: '4s',
    tags: { name: 'account-profile' },
  }), { 'account ok': (r) => ok(r, [200]) });
}

export function tiles() {
  check(http.get(`${baseUrl}/api/v1/spatial/map-overlay?layerName=hazards`, {
    timeout: '4s',
    tags: { name: 'map-overlay' },
  }), { 'overlay ok': (r) => ok(r, [200]) });
}

export function readiness() {
  check(http.get(`${baseUrl}/health/ready`, {
    timeout: '2s',
    tags: { name: 'readiness' },
  }), { 'ready ok': (r) => ok(r, [200, 503]) });
}
