import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';
import { Rate, Trend } from 'k6/metrics';

const baseUrl = __ENV.BASE_URL || 'http://127.0.0.1:5099';
const duration = __ENV.DURATION || '3m';
const routeRate = Number(__ENV.ROUTE_RATE || 8);
const preAllocatedVUs = Number(__ENV.PREALLOCATED_VUS || 80);
const maxVUs = Number(__ENV.MAX_VUS || 400);
const routeP99Ms = Number(__ENV.ROUTE_P99_MS || 2500);
const routeP95Ms = Number(__ENV.ROUTE_P95_MS || 1000);
const routeDataset = (__ENV.ROUTE_DATASET || 'birmingham').toLowerCase();
const routeDatasetFile = __ENV.ROUTE_DATASET_FILE || '';

export const routeApiFailure = new Rate('route_api_failure');
export const routeEndToEndMs = new Trend('route_end_to_end_ms', true);

export const options = {
  scenarios: {
    routing_api_p99: {
      executor: 'constant-arrival-rate',
      rate: routeRate,
      timeUnit: '1s',
      duration,
      preAllocatedVUs,
      maxVUs,
      gracefulStop: '30s',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.02'],
    checks: ['rate>0.98'],
    route_api_failure: ['rate<0.02'],
    'http_req_duration{name:route-options}': [`p(95)<${routeP95Ms}`, `p(99)<${routeP99Ms}`],
    route_end_to_end_ms: [`p(95)<${routeP95Ms}`, `p(99)<${routeP99Ms}`],
  },
  summaryTrendStats: ['min', 'avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

const birminghamRoutePairs = [
  [-1.8985, 52.4814, -1.9300, 52.4510, 'standard', 0.45, []],
  [-1.8904, 52.4862, -1.8894, 52.4862, 'standard', 0.35, []],
  [-1.8885, 52.4835, -1.8936, 52.4795, 'manual-wheelchair', 0.75, ['avoid-stairs', 'wheelchair']],
  [-1.9125, 52.4855, -1.9015, 52.4805, 'stroller', 0.65, ['avoid-stairs', 'avoid-cobblestone']],
];

const fixtureRoutePairs = [
  [-1.8904, 52.4862, -1.8894, 52.4862, 'standard', 0.35, []],
  [-1.8904, 52.4862, -1.8899, 52.4862, 'manual-wheelchair', 0.65, ['avoid-stairs', 'wheelchair']],
  [-1.8899, 52.4862, -1.8899, 52.48645, 'stroller', 0.55, ['avoid-stairs']],
  [-1.8894, 52.4862, -1.8904, 52.4862, 'standard', 0.45, []],
];

function loadRoutePairs() {
  if (routeDatasetFile) {
    const parsed = JSON.parse(open(routeDatasetFile));
    return parsed.routes.map((route) => [
      route.start.x,
      route.start.y,
      route.end.x,
      route.end.y,
      route.profile || 'standard',
      route.safetyWeight ?? 0.5,
      route.preferences || [],
    ]);
  }

  return routeDataset === 'fixture' ? fixtureRoutePairs : birminghamRoutePairs;
}

const routePairs = loadRoutePairs();

function jitter(value, width = 0.00003) {
  if (routeDataset === 'fixture' || routeDatasetFile) {
    return value;
  }

  const offset = ((exec.scenario.iterationInTest % 31) - 15) * width;
  return value + offset;
}

function routeRequest() {
  const pair = routePairs[exec.scenario.iterationInTest % routePairs.length];
  return {
    start: { x: jitter(pair[0]), y: jitter(pair[1]) },
    end: { x: jitter(pair[2]), y: jitter(pair[3]) },
    profile: pair[4],
    safetyWeight: pair[5],
    preferences: pair[6],
  };
}

export default function routingApiP99() {
  const started = Date.now();
  const response = http.post(
    `${baseUrl}/api/v1/routing/safe-path/options`,
    JSON.stringify(routeRequest()),
    {
      headers: { 'Content-Type': 'application/json' },
      timeout: '10s',
      tags: { name: 'route-options' },
    },
  );
  const elapsed = Date.now() - started;
  routeEndToEndMs.add(elapsed);

  const ok = check(response, {
    'route options status ok': (r) => r.status === 200 || r.status === 202,
    'route options has body': (r) => Boolean(r.body && r.body.length > 0),
  });
  routeApiFailure.add(!ok);
}
