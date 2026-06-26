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

Measured local West Midlands run on 2026-06-26:

```bash
CITY_NAME=west-midlands \
OSM_URL=https://download.geofabrik.de/europe/united-kingdom/england/west-midlands-latest.osm.pbf \
ROUTE_DATASET_FILE=tools/k6/birmingham-city-routes.json \
DURATION=45s \
ROUTE_RATE=4 \
ROUTE_P95_MS=300 \
ROUTE_P99_MS=1000 \
PROFILE_FLAMEGRAPH=true \
PROFILE_DURATION_SECONDS=45 \
SKIP_IMPORT=true \
tools/run-real-city-api-p99.sh
```

- import scale: 6,719,409 OSM records, 754,727 route nodes, 1,633,260 route edges
- import duration: 2m48s on local Postgres/PostGIS
- cold local-graph API run: 180 requests, 0 failures, p95 422.31 ms, p99 2,238.42 ms
- flamegraph hotspot: first requests spend time in `RouteGraphRepository.LoadGraphRegionAsync` and `RoutingService.FindSafePathFallback`
- optimized warm local-graph API run: 181 requests, 0 failures, p95 26.35 ms, p99 95.72 ms
- flamegraph artifacts: `TestResults/accesscity-west-midlands-warm-localgraph-p99-flamegraph/flamegraph/*.speedscope.json`

The real-city harness disables external OSRM by default through `Routing__ExternalOsrmEnabled=false`.
That keeps p99 focused on AccessCity's local API, cache, PostGIS, and graph-routing behavior instead of public-network latency.
Set `EXTERNAL_OSRM_ENABLED=true` when you explicitly want product-routing behavior that includes the public OSRM dependency.

The harness also warms the benchmark route set by default before k6 starts. Disable it with `WARMUP_ROUTE_CACHE=false` when measuring cold-start p99.

Run the real-city p99 concurrency matrix:

```bash
CITY_NAME=west-midlands \
OSM_URL=https://download.geofabrik.de/europe/united-kingdom/england/west-midlands-latest.osm.pbf \
ROUTE_DATASET_FILE=tools/k6/birmingham-city-routes.json \
ROUTE_RATES="4 16 64 128" \
DURATION=60s \
IMPORT_ON_FIRST_RUN=false \
tools/run-real-city-api-p99-matrix.sh
```

Artifacts:

```text
TestResults/accesscity-real-city-api-p99-matrix/api_p99_matrix_summary.json
TestResults/accesscity-real-city-api-p99-matrix/api_p99_matrix_summary.md
TestResults/accesscity-real-city-api-p99-matrix/rate-*/k6/k6-routing-api-summary.json
```

Measured local West Midlands matrix on 2026-06-26 after importing 6,719,409 OSM records:

| Target rps | Requests | Achieved rps | Failure rate | p50 ms | p95 ms | p99 ms | max ms |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 4 | 181 | 4.02 | 0.0000 | 8.70 | 121.08 | 207.24 | 392.00 |
| 16 | 721 | 16.02 | 0.0000 | 1.08 | 7.14 | 119.45 | 169.64 |
| 64 | 2,881 | 64.02 | 0.0000 | 1.04 | 6.39 | 42.00 | 233.68 |
| 128 | 5,759 | 127.98 | 0.0000 | 1.01 | 6.51 | 239.90 | 637.46 |

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
QUERIES=10000000 \
HAZARDS=1000000 \
BATCH_SIZE=512 \
GRID_CELLS=262144 \
tools/run-cpp-kernel-benchmark.sh
```

Artifacts:

```text
TestResults/accesscity-cpp-kernel/cpp_kernel_report.json
```

This comparison is intentionally narrow. It answers whether the distance kernel is competitive with an optimized native implementation; it does not claim that the whole routing system is C++/HFT-equivalent.

The C++ comparison now also reports dense risk-grid lookup p50/p95/p99 so the project has a native baseline for the same kind of hot path used by city-scale risk lookup.
On Linux, add `PERF_STAT=true` to capture cycles, instructions, cache misses, and branch misses in `cpp_kernel_perf_stat.txt`.

Measured local C++ run on 2026-06-26:

- hazards: 1,000,000
- queries: 10,000,000
- grid build: 12.4846 ms
- distance kernel: p50 0.0098 us, p95 0.0195 us, p99 0.0644 us, 49,783,875 ops/s
- dense-grid lookup: p50 0.0072 us, p95 0.0138 us, p99 0.0986 us, 52,229,321 ops/s

Measured local C++ run with 10-thread parallel lookup on 2026-06-26:

- hazards: 1,000,000
- queries: 10,000,000
- grid build: 4.0344 ms
- distance kernel: p50 0.0061 us, p95 0.0131 us, p99 0.0265 us, 126,237,190 ops/s
- dense-grid lookup: p50 0.0059 us, p95 0.0069 us, p99 0.0194 us, 185,411,081 ops/s
- parallel dense-grid lookup: 10 threads, 2,106,075,881 ops/s, 11.359x vs single-thread

## Linux Perf Counters

Run hardware counter and flamegraph-source captures on Linux:

```bash
QUERIES=10000000 \
HAZARDS=1000000 \
MESSAGES=1000000 \
PRODUCER_CPU=0 \
CONSUMER_CPU=1 \
tools/run-linux-perf-cpp-benchmarks.sh
```

Artifacts:

```text
TestResults/accesscity-linux-perf/risk_kernel_perf_stat.csv
TestResults/accesscity-linux-perf/risk_kernel_perf.data
TestResults/accesscity-linux-perf/risk_kernel_perf_report.txt
TestResults/accesscity-linux-perf/market_data_replay_perf_stat.csv
TestResults/accesscity-linux-perf/market_data_replay_perf.data
TestResults/accesscity-linux-perf/market_data_replay_perf_report.txt
TestResults/accesscity-linux-perf/linux_perf_summary.json
```

The summary computes cycles/query, IPC, cache miss rate, and branch miss rate from `perf stat -x,`.
`perf record -F 999 -g` captures sampled call stacks for the C++ spatial kernel and lock-free replay path.
This script intentionally exits on non-Linux hosts because macOS does not expose Linux perf counters.
It also exits when `/proc/sys/kernel/perf_event_paranoid` blocks unprivileged profiling; set it to `-1`
or grant the relevant perf capabilities before using these numbers in public claims.

## Market-Data Style C++ Replay and Network Ingest

Run the lock-free SPSC replay plus TCP/UDP loopback ingest benchmark:

```bash
MESSAGES=1000000 \
NETWORK_MESSAGES=100000 \
PRODUCER_CPU=-1 \
CONSUMER_CPU=-1 \
tools/run-market-data-benchmark.sh
```

Use `PRODUCER_CPU` and `CONSUMER_CPU` on Linux to pin the producer and consumer threads.

Artifacts:

```text
TestResults/accesscity-market-data/spsc_replay_report.json
TestResults/accesscity-market-data/udp_loopback_report.json
TestResults/accesscity-market-data/tcp_loopback_report.json
TestResults/accesscity-market-data/market_data_summary.json
TestResults/accesscity-market-data/market_data_summary.md
```

Measured local macOS run on 2026-06-26:

| Mode | Messages | Received | Losses | Throughput msg/s | p50 ns | p95 ns | p99 ns | max ns |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| spsc_replay | 1,000,000 | 1,000,000 | 0 | 11,210,872 | 43,458 | 152,042 | 170,209 | 287,542 |
| udp_loopback | 100,000 | 100,000 | 0 | 93,852 | 18,541 | 78,459 | 1,344,292 | 49,173,875 |
| tcp_loopback | 100,000 | 100,000 | 0 | 229,806 | 21,500 | 110,584 | 334,792 | 1,044,541 |

The macOS loopback numbers are useful smoke evidence, not hardware-counter evidence.
For low-latency claims, prefer the Linux perf path with CPU pinning and dedicated cores.

Measured remote Ubuntu 24.04 / NVIDIA L20 host run on 2026-06-26:

| Mode | Messages | Received | Losses | Throughput msg/s | p50 ns | p95 ns | p99 ns | max ns |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| spsc_replay | 1,000,000 | 1,000,000 | 0 | 7,614,646 | 125,543 | 131,199 | 137,901 | 3,470,761 |
| udp_loopback | 100,000 | 100,000 | 0 | 260,369 | 7,137 | 12,042 | 12,917 | 84,406 |
| tcp_loopback | 100,000 | 100,000 | 0 | 193,837 | 10,110 | 13,589 | 18,123 | 195,693 |

Linux hardware counters were not captured on that host because `perf_event_paranoid=4` and the user did not have sudo access to lower it.

## City-Scale Hot-Path Benchmark

Run the 10M-query city benchmark:

```bash
dotnet AccessCity.SoakTestRunner/bin/Release/net9.0/AccessCity.SoakTestRunner.dll \
  city-benchmark \
  --hazards 1000000 \
  --queries 10000000 \
  --rounds 1 \
  --rtree-samples 20000 \
  --batch-size 512 \
  --max-grid-p99-us 50 \
  --max-distance-p99-us 10
```

Measured local run on 2026-06-26:

- hazards: 1,000,000
- queries: 10,000,000
- H3 risk lookup: p50 0.005 us, p95 0.0107 us, p99 0.027 us, 137,319,631 ops/s, 0 allocated bytes per lookup
- distance kernel: p50 0.0073 us, p95 0.0202 us, p99 0.0372 us, 95,576,527 ops/s, 0 allocated bytes per call
- R-tree nearby query sample: p95 720.2379 us, p99 764.2852 us
- gate: passed

This is an algorithm hot-path benchmark. It supports low-latency claims for risk lookup and distance kernels, while the real-city API p99 section remains the system-level latency claim.

## Production Observability

The API emits OpenTelemetry metrics for route latency, route computation capacity, cache hit/miss pressure,
external dependencies, and route graph load latency/edge counts. Prometheus scrapes the OTEL collector through:

```text
deploy/observability/prometheus.yml
```

Grafana starter dashboard:

```text
deploy/observability/grafana-accesscity-performance-dashboard.json
```

For p99 optimization, watch `accesscity_route_graph_load_duration_milliseconds_*` by `route_graph_source`.
Cold p99 should show PostGIS or file-artifact load time; warm steady-state should shift toward memory hits.

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
