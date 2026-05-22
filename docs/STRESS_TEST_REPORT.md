# API Performance & Stress Test Report (Milestone 4 Final)

**Date**: 2026-03-23
**Target Environment**: Local Production-Ready (Dockerized API + PostGIS + Redis)
**Base URL**: `http://localhost:8080`

---

## 2026-05-22 Post-Scalability-Guard Regression

After adding scoped PostGIS hazard reads, EF Core context pooling, external dependency bulkheads,
and the shared route computation limiter, the Release test run passed:

- Full regression: 172/172 tests passed.
- Stress/benchmark subset: 9/9 tests passed.
- Safe-path cold start: 631 ms.
- Safe-path warm cache hit: 4 ms.
- Mixed endpoint load: 25/25 success, p50 11.52 ms, p95 65.97 ms, p99 66.09 ms, 378.1 req/s wall-clock throughput.
- Dashboard burst: 15/15 success, p50 116.58 ms, p95 119.55 ms.
- Spatial POI burst: 20/20 success, p50 16.63 ms, p95 70.82 ms.
- Hazards burst: 16/16 success, p50 0.76 ms, p95 1.13 ms.

This is still local API/Postgres validation. Real horizontal-scaling validation should use the
Kubernetes k6 job documented in `docs/DISTRIBUTED_LOAD_TESTING.md`.

---

## Executive Summary

Following the "Perfect Engineering Overhaul," the AccessCity API has been matured through architectural optimizations including **Request Coalescing**, **Asynchronous Job Queues**, and **Concurrency Gating**. Final stress testing (v1.2) demonstrates a **7.5x reduction** in safe-path latency and a **114% increase** in total throughput compared to the baseline. The system now maintains a **99.86% success rate** under a sustained 100-VU concurrent load.

---

## 📊 Summary Metrics (v1.2 Final)

| Metric | Baseline (v1.0) | Final Overhaul (v1.2) | Change |
| :--- | :--- | :--- | :--- |
| **Total Requests** | 12,109 | **26,019** | +114% ↑ |
| **Throughput** | 56.8 req/s | **123.1 req/s** | +116% ↑ |
| **Error Rate** | 2.30% | **0.27%** | -88% ↓ |
| **Success Rate** | 97.7% | **99.86%** | +2.2% ↑ |
| **p95 Safe-Path** | 3,450ms | **458ms** | **7.5x Faster** 🚀 |

### End-to-End Latency (p95)
- **Health Checks**: 36.9ms
- **Dashboard Summary**: 4.2ms
- **Spatial (POI/Overlay)**: 5.3ms
- **Routing Options**: 6.2ms
- **Safety-Weighted Path**: 458ms (Average: 121ms)
- **Auth (Login/Reg)**: 183ms

---

## 🏗 Architectural Optimization Analysis

### 1. Request Coalescing
Redundant routing computations for identical start/end coordinates within a 500ms window are now collapsed into a single execution. Under high concurrency, this drastically reduced CPU contention, as multiple Virtual Users (VUs) benefited from the first-execution result.

### 2. Concurrency Control (SemaphoreSlim)
By implementing a 4-slot concurrency gate for heavy A* calculations, we protected the database connection pool from exhaustion. This was the primary driver for the jump from a 35% safe-path success rate (v1.0) to **99.86%** (v1.2).

### 3. Asynchronous Job Pipeline
The transition to a non-blocking background queue ensured that the API remained responsive to new requests even when the compute gate was saturated, effectively eliminating HTTP time-outs.

---

## 🚧 Limitations & Critical Evaluation

This evaluation was conducted in a controlled local environment using a single-node deployment. External API latency (OSRM), network variability, and distributed load balancing effects were not captured. Additionally, improvements observed in routing performance are heavily influenced by request coalescing and may not fully generalise to highly diverse, non-repetitive routing queries in a larger city-wide scope.

Further testing in a multi-node environment with simulated network jitter would be required to validate horizontal scaling characteristics.

---

## Conclusion

The system demonstrates significant performance improvements following architectural optimisations, with throughput increasing to 123 req/s and safe-path latency reduced to sub-second p95 under controlled load.

However, these results are obtained in a single-node, local environment and may not fully reflect behaviour under distributed production conditions. While request coalescing and concurrency control mechanisms have effectively reduced contention, further validation under larger-scale and multi-node deployments is required. 

Overall, the system shows strong progress toward production readiness, but additional testing and optimisation would be necessary before real-world deployment.
