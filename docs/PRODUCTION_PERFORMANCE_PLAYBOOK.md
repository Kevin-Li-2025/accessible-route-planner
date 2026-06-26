# Production Performance Playbook

This playbook covers the harder performance artifacts used to validate AccessCity beyond unit tests:

- full HTTP routing API p95/p99 under load
- BenchmarkDotNet hot-path microbenchmarks
- CPU flamegraph capture for the live API
- NativeAOT benchmark publishing
- C++ kernel comparison for the distance hot path

## Routing API p99

Run the API and then execute:

```bash
BASE_URL=http://127.0.0.1:5099 \
DURATION=3m \
ROUTE_RATE=8 \
ROUTE_P95_MS=1000 \
ROUTE_P99_MS=2500 \
tools/run-routing-api-p99-test.sh
```

Artifacts:

```text
TestResults/accesscity-routing-api-p99/k6-routing-api-summary.json
```

This measures full HTTP latency for `POST /api/v1/routing/safe-path/options`, including serialization, controller work, routing service work, cache/coalescing behavior, and response serialization.

For the checked-in tiny OSM fixture, pass `ROUTE_DATASET=fixture` so every request lands on the imported graph:

```bash
BASE_URL=http://127.0.0.1:5099 \
ROUTE_DATASET=fixture \
DURATION=60s \
ROUTE_RATE=8 \
tools/run-routing-api-p99-test.sh
```

## Real City API p99

Run the full local city pipeline:

```bash
DURATION=30s \
ROUTE_RATE=4 \
ROUTE_P95_MS=2000 \
ROUTE_P99_MS=5000 \
tools/run-real-city-api-p99.sh
```

Default scope is Monaco because it is a complete real city OSM PBF that is small enough for repeatable local validation.
Override `OSM_URL`, `CITY_NAME`, and `ROUTE_DATASET_FILE` for Birmingham, London, or another larger city extract.

Artifacts:

```text
TestResults/accesscity-real-city-api-p99/osm-import-response.json
TestResults/accesscity-real-city-api-p99/route-graph-status.json
TestResults/accesscity-real-city-api-p99/k6/k6-routing-api-summary.json
TestResults/accesscity-real-city-api-p99/real_city_api_p99_report.json
TestResults/accesscity-real-city-api-p99/real_city_api_p99_report.md
```

Measured local Monaco run on 2026-06-26:

- route graph: 13,470 nodes / 29,486 edges
- traffic: 120 requests at 4 rps for 30s
- failures: 0
- HTTP p95/p99: 476.87 ms / 1,555.71 ms
- end-to-end p95/p99: 477.15 ms / 1,556.57 ms

## SLO Gate

Convert a k6 summary to a machine-readable SLO verdict:

```bash
SLO_ROUTE_P95_MS=500 \
SLO_ROUTE_P99_MS=2000 \
SLO_ROUTE_FAILURE_RATE=0.001 \
node tools/evaluate-routing-slo.js \
  TestResults/accesscity-real-city-api-p99/k6/k6-routing-api-summary.json \
  TestResults/accesscity-real-city-api-p99/routing_slo_report.json
```

## BenchmarkDotNet

Run the dedicated benchmark project:

```bash
tools/run-benchmarkdotnet-hotpath.sh
```

The benchmark uses `MemoryDiagnoser`, `ThreadingDiagnoser`, p50/p95/max columns, and JSON export. Use it when comparing runtime, JIT, or hot-path implementation changes.

If `dotnet` is not on `PATH`, set:

```bash
DOTNET_CLI=/usr/local/share/dotnet/dotnet tools/run-benchmarkdotnet-hotpath.sh
```

## CPU Flamegraph

Install `dotnet-trace` once:

```bash
dotnet tool install --global dotnet-trace
```

Find the API process and collect a CPU sample:

```bash
pgrep -fl AccessCity.API
DURATION_SECONDS=60 tools/profile-routing-api-flamegraph.sh <pid>
```

Artifacts:

```text
TestResults/accesscity-flamegraph/*.nettrace
TestResults/accesscity-flamegraph/*.speedscope.json
```

Open the speedscope file at `https://www.speedscope.app/`.

## NativeAOT Benchmark

Publish and run the benchmark executable with NativeAOT:

```bash
RUNTIME=osx-arm64 tools/run-native-aot-benchmark.sh
```

Use `RUNTIME=linux-x64` on CI or a Linux server.

NativeAOT is applied to `AccessCity.NativeAotKernels`, not the ASP.NET API. That keeps the comparison focused on deterministic routing/risk kernels without conflating it with web-hosting compatibility constraints. The benchmark now reports both the distance kernel and a dense risk-grid lookup kernel.

## C++ Kernel Comparison

Compile and run the C++ equirectangular distance kernel:

```bash
QUERIES=1000000 BATCH_SIZE=256 tools/run-cpp-kernel-benchmark.sh
```

Artifacts:

```text
TestResults/accesscity-cpp-kernel/cpp_kernel_report.json
```

This comparison is intentionally narrow. It answers whether the distance kernel is competitive with an optimized native implementation; it does not claim that the whole routing system is C++/HFT-equivalent.

The C++ comparison now also reports dense risk-grid lookup p50/p95/p99 so the project has a native baseline for the same kind of hot path used by city-scale risk lookup.

## AI Model Evaluation

The accessibility planning endpoint uses a local auditable ranking model:

```text
AccessCity.API/Models/accessibility_repair_ranker_v1.json
```

Run the model eval through the backend test suite:

```bash
dotnet test AccessCity.Tests/AccessCity.Tests.csproj \
  --configuration Release \
  --filter FullyQualifiedName~AccessibilityPlanning
```

Artifact:

```text
TestResults/accesscity-ai-model-eval/accessibility_ranker_eval_report.json
```

The model is deliberately local and explainable: every repair candidate includes `modelScore`, `modelConfidence`, and per-feature contributions.

## Interpretation Boundaries

- API p99 is the system-level number to use for product and production claims.
- BenchmarkDotNet and C++ results are kernel-level numbers.
- Flamegraphs explain where CPU time goes under representative traffic.
- NativeAOT results show runtime overhead sensitivity for the benchmark kernels.
- City-scale hot-path benchmarks prove edge-risk lookup behavior, not full route latency by themselves.
