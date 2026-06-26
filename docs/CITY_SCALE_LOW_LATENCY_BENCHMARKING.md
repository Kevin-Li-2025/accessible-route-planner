# City-Scale Low-Latency Benchmarking

AccessCity has a repeatable city-scale benchmark for the routing hot path:

- snapshot-swapped spatial hazard index rebuilds
- sparse H3 risk grid rebuilds
- allocation-free dense accelerator lookups for city-scale snapshots
- per-edge O(1) risk lookups used by A*/ALT route costing
- distance-kernel latency used by route heuristics
- JSON and Markdown reports for CI artifacts
- batched latency sampling for sub-microsecond hot paths

This is a hot-path benchmark, not an end-to-end production capacity claim. It is intended to make routing performance regressions visible before they reach the API layer.

## CI Gate

GitHub Actions runs:

```bash
dotnet run --project AccessCity.SoakTestRunner/AccessCity.SoakTestRunner.csproj \
  --configuration Release \
  --no-build \
  -- \
  city-benchmark \
  --hazards 50000 \
  --queries 250000 \
  --batch-size 256 \
  --rtree-samples 10000 \
  --max-grid-p99-us 25 \
  --max-distance-p99-us 10
```

The gate fails if H3 risk lookup p99 or distance-kernel p99 exceeds the configured threshold. Reports are written to:

```text
TestResults/accesscity-city-benchmark/city_benchmark_report.json
TestResults/accesscity-city-benchmark/city_benchmark_report.md
```

## Larger Local Runs

For a heavier local run:

```bash
dotnet run --project AccessCity.SoakTestRunner/AccessCity.SoakTestRunner.csproj \
  --configuration Release \
  -- \
  city-benchmark \
  --hazards 250000 \
  --queries 1000000 \
  --rounds 5 \
  --batch-size 256 \
  --rtree-samples 25000 \
  --max-grid-p99-us 25 \
  --max-distance-p99-us 10
```

This produces 5 million H3 lookups over a 250k-hazard synthetic city workload. Use this on a dedicated machine when comparing runtime versions, graph preprocessing changes, or risk-grid implementations.

## Why This Matters

The route planner's production hot path should not scan hazards linearly for every edge. The benchmark makes that invariant measurable:

- spatial index rebuild cost stays visible
- H3 lookup p50/p95/p99 stays visible
- distance kernel latency stays visible
- allocation per query stays visible
- CI blocks obvious hot-path regressions

Latency samples are measured in batches and divided by operation count. This avoids over-weighting `Stopwatch` overhead when individual H3 or distance-kernel operations complete below normal timer granularity.

The grid is hybrid:

- city/metropolitan extents build a dense accelerator for allocation-free risk lookup
- wider extents retain sparse H3 fallback to avoid large dense arrays

This intentionally shifts cost from read-time route expansion to snapshot rebuild time. That is the right tradeoff for route search, where each request may evaluate many graph edges while hazard snapshots change far less frequently than edge-cost reads.

The companion route evaluation harness validates route quality and user-visible behavior. This benchmark validates the low-level latency budget behind those routes.
