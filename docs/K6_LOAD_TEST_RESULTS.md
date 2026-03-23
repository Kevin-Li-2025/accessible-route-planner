# k6 Load Test Results — AccessCity API

**Date**: 2026-03-23
**Base URL**: `http://localhost:8080`
**Tool**: k6 v1.6.1 (Grafana Labs)
**Rate Limiter**: 10,000 req/min (relaxed for testing)

---

## Load Profile

| Phase | Duration | Virtual Users |
|-------|----------|---------------|
| Ramp-up | 30s | 0 → 50 |
| Sustained | 2m | 50 |
| Spike | 30s | 50 → 100 |
| Cool-down | 30s | 100 → 0 |

**Total Duration**: 3m 35s
**Total Iterations**: 2,166
**Max Concurrent Users**: 100

---

## Key Metrics

| Metric | Value |
|--------|-------|
| **Total Requests** | 19,911 |
| **RPS (avg)** | 92.4 req/s |
| **Latency p50** | 1.30 ms |
| **Latency p90** | 16.48 ms |
| **Latency p95** | 22.61 ms |
| **Error Rate** | **0.81%** |
| **HTTP Failures** | **0 / 19,911 (0.00%)** |
| **Data Received** | 14 MB (66 KB/s) |

---

## Per-Endpoint Latency

| Endpoint | Median | p90 | p95 | Max |
|----------|--------|-----|-----|-----|
| Health | 17.81 ms | 28.72 ms | 34.30 ms | 350.53 ms |
| Dashboard | 0.95 ms | 2.75 ms | 3.69 ms | 10.91 s |
| Spatial | 1.26 ms | 3.52 ms | 4.57 ms | 172.52 ms |
| Risk Score | 1.58 ms | 4.21 ms | 5.23 ms | 25.86 s |
| Hazards | 0.73 ms | 2.27 ms | 3.32 ms | 78.98 ms |
| Auth (register) | 83.92 ms | 133.47 ms | 157.32 ms | 606.11 ms |
| **Safe Path** | **22.91 s** | **30.01 s** | **30.01 s** | **30.02 s** |

---

## Threshold Results

| Threshold | Criteria | Result |
|-----------|----------|--------|
| Global latency | p(95) < 3,000ms | ✅ PASS (22.61ms) |
| Health latency | p(95) < 500ms | ✅ PASS (34.30ms) |
| Dashboard latency | p(95) < 1,000ms | ✅ PASS (3.69ms) |
| Spatial latency | p(95) < 500ms | ✅ PASS (4.57ms) |
| Error rate | < 10% | ✅ PASS (0.81%) |

---

## Conclusion

All five performance thresholds **passed**. HTTP failure rate is **0%** — every request received a valid response. The 0.81% error rate is from timeouts on compute-heavy safe-path routing (A\* pathfinding, median 22.9s under concurrent load). Read endpoints maintain sub-5ms p95 latency at 92.4 req/s with 100 concurrent users, demonstrating effective caching. Safe-path routing requires optimization for concurrent scalability.
