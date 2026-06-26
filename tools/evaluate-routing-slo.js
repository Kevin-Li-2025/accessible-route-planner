#!/usr/bin/env node
const fs = require('fs');

const summaryPath = process.argv[2] || 'TestResults/accesscity-routing-api-p99/k6-routing-api-summary.json';
const outPath = process.argv[3] || 'TestResults/accesscity-routing-api-p99/routing_slo_report.json';
const p95TargetMs = Number(process.env.SLO_ROUTE_P95_MS || 250);
const p99TargetMs = Number(process.env.SLO_ROUTE_P99_MS || 1000);
const failureTarget = Number(process.env.SLO_ROUTE_FAILURE_RATE || 0.001);

const summary = JSON.parse(fs.readFileSync(summaryPath, 'utf8'));
const routeMetric = summary.metrics['http_req_duration{name:route-options}'] || summary.metrics.http_req_duration || {};
const failureMetric = summary.metrics.route_api_failure || summary.metrics.http_req_failed || {};
const checks = summary.metrics.checks || {};
const httpReqs = summary.metrics.http_reqs || {};

const report = {
  harnessVersion: 'accesscity-routing-slo-gate-v1',
  generatedAtUtc: new Date().toISOString(),
  input: summaryPath,
  requestCount: httpReqs.count || 0,
  requestRate: httpReqs.rate || 0,
  targets: {
    p95Ms: p95TargetMs,
    p99Ms: p99TargetMs,
    failureRate: failureTarget
  },
  observed: {
    p50Ms: routeMetric.med || 0,
    p95Ms: routeMetric['p(95)'] || 0,
    p99Ms: routeMetric['p(99)'] || 0,
    maxMs: routeMetric.max || 0,
    failureRate: failureMetric.value || 0,
    checkRate: checks.value || 0
  }
};

report.passed = report.observed.p95Ms <= p95TargetMs
  && report.observed.p99Ms <= p99TargetMs
  && report.observed.failureRate <= failureTarget
  && report.observed.checkRate >= 1 - failureTarget;
report.failures = [];
if (report.observed.p95Ms > p95TargetMs) report.failures.push(`p95 ${report.observed.p95Ms}ms > ${p95TargetMs}ms`);
if (report.observed.p99Ms > p99TargetMs) report.failures.push(`p99 ${report.observed.p99Ms}ms > ${p99TargetMs}ms`);
if (report.observed.failureRate > failureTarget) report.failures.push(`failure rate ${report.observed.failureRate} > ${failureTarget}`);
if (report.observed.checkRate < 1 - failureTarget) report.failures.push(`check rate ${report.observed.checkRate} < ${1 - failureTarget}`);

fs.mkdirSync(require('path').dirname(outPath), { recursive: true });
fs.writeFileSync(outPath, JSON.stringify(report, null, 2));
console.log(JSON.stringify(report, null, 2));
process.exitCode = report.passed ? 0 : 1;
