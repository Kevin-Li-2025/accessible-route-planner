# k6 Load Test Results — AccessCity API

## Distributed Kubernetes + PgBouncer Run

**Date**: 2026-05-23
**Cluster**: `kind-accesscity-multi` / `accesscity-ha`
**Topology**: 3 Kubernetes nodes, 6 API pods, 2 worker pods, 3 Postgres instances, 3 CNPG PgBouncer pooler pods, 3 Kafka brokers, Redis cache
**Database path**: API and worker pods use `accesscity-postgres-rw-pooler`; migration jobs keep a direct primary URL.

### Load Profile

| Phase | Duration | Virtual Users |
|-------|----------|---------------|
| Ramp-up | 30s | 0 -> 20 |
| Sustained | 2m | 20 |
| Ramp-up | 30s | 20 -> 50 |
| Sustained | 2m | 50 |
| Cool-down | 30s | 50 -> 0 |

### Summary

| Metric | Value |
|--------|-------|
| Total requests | 145,334 |
| Throughput | 440.36 req/s |
| Check success | 100.00% |
| HTTP failures | 0.00% |
| Overall latency p95 | 51.59 ms |
| Risk-score p95 | 8.25 ms |
| Safe-path p95 | 167.56 ms |
| Iterations | 44,718 |
| Postgres backend connections during run | 35-37 |

### Result

The distributed path passed with zero failed checks and zero HTTP failures while keeping direct
Postgres backend connections bounded behind PgBouncer. This verifies the current multi-replica API,
worker, Kafka, Redis, PostGIS, and pooler path under the checked-in k6 scenario.

---

# Historical Local k6 Load Test Results — AccessCity API (Final v1.2)

**Date**: 2026-03-23
**Base URL**: `http://localhost:8080`
**Tool**: k6 v1.6.1 (Grafana Labs)
**Rate Limiter**: 10,000 req/min (Burst Verification Mode)

---

## Load Profile

| Phase | Duration | Virtual Users |
|-------|----------|---------------|
| Ramp-up | 30s | 0 → 50 |
| Sustained | 2m | 50 |
| Spike | 30s | 50 → 100 |
| Cool-down | 30s | 100 → 0 |

- **Total Duration**: 3m 31s
- **Total Iterations**: 2,824
- **Max Concurrent Users**: 100

---

## Performance Summary

| Metric | Value | Result |
|--------|-------|--------|
| **Total Requests** | **26,019** | +40% vs v1.0 |
| **RPS (avg)** | **123.1 req/s** | Excellent |
| **Latency p50** | 1.39 ms | Sub-millisecond med |
| **Latency p95** | 24.78 ms | Global response avg |
| **Error Rate** | **0.27%** | Near zero |
| **Success Rate** | **99.86%** | High Resilience |
| **HTTP Failures** | **0** | No drop-offs |
| **Data Received** | 19 MB (89 KB/s) | Balanced |

---

## Domain Latency (p95)

- **Health Checks**: 36.9 ms
- **Dashboard API**: 4.2 ms
- **Spatial / POI**: 5.3 ms
- **Routing Risk Score**: 6.2 ms
- **Safety-Weighted Path**: **458.1 ms** (Average: 121 ms)

---

## Threshold Verification

| Threshold | Definition | Result |
|-----------|------------|--------|
| `http_req_duration` | p(95) < 3,000ms | ✅ PASS (24ms) |
| `health_latency` | p(95) < 500ms | ✅ PASS (36ms) |
| `dashboard_latency` | p(95) < 1,000ms | ✅ PASS (4.2ms) |
| `spatial_latency` | p(95) < 500ms | ✅ PASS (5.3ms) |
| `error_rate` | rate < 0.1 (10%) | ✅ PASS (0.27%) |

---

## Conclusion

Following architectural hardening, the API now handles 100 concurrent users with **zero** HTTP level failures and a total check success rate of **99.86%**. The throughput bottleneck in safe-path routing was successfully resolved through **Request Coalescing** and **Concurrency Gating**, bringing p95 latency down to ~458ms. The AccessCity API is verified for final Milestone 4 submission.
