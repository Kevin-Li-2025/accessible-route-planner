using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AccessCity.API.Models;
using AccessCity.API.Services;
using NetTopologySuite.Geometries;

namespace AccessCity.SoakTestRunner;

public static class Program
{
    private static readonly double BaseLatitude = 52.4862;
    private static readonly double BaseLongitude = -1.8904;

    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "city-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            RunCityBenchmark(args.Skip(1).ToArray());
            return;
        }

        // Parse duration (default: 20 minutes)
        int durationMinutes = 20;
        if (args.Length > 0 && int.TryParse(args[0], out int customMinutes))
        {
            durationMinutes = customMinutes;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==========================================================");
        Console.WriteLine("        ACCESSCITY ARCHITECTURAL ENDURANCE SOAK RUN       ");
        Console.WriteLine("==========================================================");
        Console.ResetColor();
        Console.WriteLine($"* Duration: {durationMinutes} minutes");
        Console.WriteLine($"* Target Environment: .NET 9 Core, In-Memory Multi-Threaded");
        Console.WriteLine($"* Threading Setup: 16 Reader Threads, 1 Grid Rebuilder Thread");
        Console.WriteLine($"* H3 Indexing: Resolution 9 Sparse Hashed Matrix");
        Console.WriteLine("----------------------------------------------------------");

        var random = new Random(42);
        var spatialIndex = new HazardSpatialIndex();
        var riskGrid = new H3HazardRiskGrid();

        // Generate query pool
        var queryPoints = new List<(double Lat, double Lon)>();
        for (int i = 0; i < 50000; i++)
        {
            double latOffset = (random.NextDouble() - 0.5) * 0.2;
            double lonOffset = (random.NextDouble() - 0.5) * 0.2;
            queryPoints.Add((BaseLatitude + latOffset, BaseLongitude + lonOffset));
        }

        List<HazardReport> GenerateHazards(int count)
        {
            var list = new List<HazardReport>();
            for (int i = 0; i < count; i++)
            {
                double latOffset = (random.NextDouble() - 0.5) * 0.3;
                double lonOffset = (random.NextDouble() - 0.5) * 0.3;
                list.Add(new HazardReport
                {
                    Id = Guid.NewGuid(),
                    Location = new Point(BaseLongitude + lonOffset, BaseLatitude + latOffset) { SRID = 4326 },
                    Type = random.Next(3) switch
                    {
                        0 => "broken_sidewalk",
                        1 => "pothole",
                        _ => "obstruction"
                    },
                    Status = HazardStatus.Reported
                });
            }
            return list;
        }

        // Init rebuild
        spatialIndex.Rebuild(GenerateHazards(2000));
        riskGrid.Rebuild(spatialIndex);

        var cts = new CancellationTokenSource();
        var endTime = DateTime.UtcNow.AddMinutes(durationMinutes);

        long totalReads = 0;
        long totalRebuilds = 0;
        long totalErrors = 0;

        // Background Writer Thread (Continuous 50ms database sharded ingestion simulation)
        var writerTask = Task.Run(async () =>
        {
            var writerRand = new Random(999);
            while (!cts.Token.IsCancellationRequested && DateTime.UtcNow < endTime)
            {
                try
                {
                    var freshHazards = GenerateHazards(2000 + writerRand.Next(1000));
                    spatialIndex.Rebuild(freshHazards);
                    riskGrid.Rebuild(spatialIndex);
                    Interlocked.Increment(ref totalRebuilds);
                }
                catch
                {
                    Interlocked.Increment(ref totalErrors);
                }
                await Task.Delay(50);
            }
        });

        // Parallel Reader Threads
        const int numReaders = 16;
        var readerTasks = new List<Task>();
        for (int r = 0; r < numReaders; r++)
        {
            int readerId = r;
            readerTasks.Add(Task.Run(() =>
            {
                var localRand = new Random(readerId);
                while (!cts.Token.IsCancellationRequested && DateTime.UtcNow < endTime)
                {
                    var pt = queryPoints[localRand.Next(queryPoints.Count)];
                    try
                    {
                        double val = riskGrid.GetRisk(pt.Lat, pt.Lon);
                        if (val < 0 || val > 1.0)
                        {
                            Interlocked.Increment(ref totalErrors);
                        }
                        Interlocked.Increment(ref totalReads);
                    }
                    catch
                    {
                        Interlocked.Increment(ref totalErrors);
                    }
                }
            }));
        }

        // Monitoring Task
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "soak_test_log.csv");
        File.WriteAllText(logPath, "Timestamp,DurationSec,TotalReads,ThroughputOps,Rebuilds,MemoryMB,Gen0,Gen1,Gen2,Errors\n");

        var startSw = Stopwatch.StartNew();
        long lastReads = 0;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("| Time Elapsed | Total Queries | Throughput (ops/s) | Rebuilds | Heap Memory | Gen 0/1/2 | Errors |");
        Console.WriteLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");
        Console.ResetColor();

        var reportBuffer = new List<string>();

        while (DateTime.UtcNow < endTime && !cts.Token.IsCancellationRequested)
        {
            await Task.Delay(10000); // Sample every 10 seconds

            double elapsedSec = startSw.Elapsed.TotalSeconds;
            long currentReads = Interlocked.Read(ref totalReads);
            long readsDiff = currentReads - lastReads;
            double currentOps = readsDiff / 10.0;
            lastReads = currentReads;

            long memoryBytes = GC.GetTotalMemory(forceFullCollection: false);
            double memoryMb = memoryBytes / 1024.0 / 1024.0;

            int g0 = GC.CollectionCount(0);
            int g1 = GC.CollectionCount(1);
            int g2 = GC.CollectionCount(2);

            long currentRebuilds = Interlocked.Read(ref totalRebuilds);
            long currentErrors = Interlocked.Read(ref totalErrors);

            string elapsedStr = TimeSpan.FromSeconds(elapsedSec).ToString(@"hh\:mm\:ss");

            string consoleLine = $"| {elapsedStr} | {currentReads:N0} | {currentOps:N0} ops/s | {currentRebuilds} | {memoryMb:F2} MB | {g0}/{g1}/{g2} | {currentErrors} |";
            Console.WriteLine(consoleLine);
            reportBuffer.Add(consoleLine);

            File.AppendAllText(logPath, $"{DateTime.UtcNow:s},{elapsedSec:F1},{currentReads},{currentOps:F0},{currentRebuilds},{memoryMb:F2},{g0},{g1},{g2},{currentErrors}\n");
        }

        cts.Cancel();
        await Task.WhenAll(readerTasks.Concat(new[] { writerTask }));
        startSw.Stop();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("==========================================================");
        Console.WriteLine("             SOAK TEST COMPLETED SUCCESSFULLY             ");
        Console.WriteLine("==========================================================");
        Console.ResetColor();

        // Generate Final Markdown Report
        string reportPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "soak_test_report.md");
        string markdownReport = $@"# AccessCity Long-Duration Soak Test Report

## Execution Context
- **Total Duration**: {startSw.Elapsed:c}
- **Target**: H3HexagonalSparseGrid & STRtree Spatial Index
- **Ingestion/Rebuilder Rate**: 1 Rebuild every 50ms (~20 updates/sec)
- **Concurrency Pressure**: {numReaders} Reader Threads

## Overall Metrics
- **Total In-Memory Queries**: {totalReads:N0} ops
- **Final Throughput**: {totalReads / startSw.Elapsed.TotalSeconds:N0} ops/sec
- **Total Background Rebuilds**: {totalRebuilds} cycles
- **Accumulated Exceptions/Errors**: {totalErrors}
- **Final Memory Footprint**: {GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0:F2} MB

## Live Profiling Log Samples
| Time Elapsed | Total Queries | Throughput | Rebuilds | Heap Memory | Gen 0/1/2 | Errors |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
" + string.Join("\n", reportBuffer) + $@"

## Architectural Verdict
The system completed a full {durationMinutes}-minute long-duration soak test with **{totalErrors} accumulated errors**.
The snapshot-swap architecture sustained **{totalReads / startSw.Elapsed.TotalSeconds:N0} queries per second** while rebuilding the spatial index and risk grid in the background.
Treat this as a repeatable local performance artifact rather than an end-to-end production capacity claim.
";
        File.WriteAllText(reportPath, markdownReport);

        // Copy to the invocation directory for easy viewing across macOS, Linux, and Windows.
        string workspaceReportPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            $"soak_test_{durationMinutes}min_report.md");
        File.WriteAllText(workspaceReportPath, markdownReport);

        Console.WriteLine($"* Final Report generated: {workspaceReportPath}");
        Console.WriteLine($"* Log CSV saved: {logPath}");
    }

    private static void RunCityBenchmark(string[] args)
    {
        var options = CityBenchmarkOptions.Parse(args);
        Console.WriteLine("==========================================================");
        Console.WriteLine("        ACCESSCITY CITY-SCALE LOW-LATENCY BENCHMARK       ");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"* City bbox: {options.MinLat:F4},{options.MinLon:F4} -> {options.MaxLat:F4},{options.MaxLon:F4}");
        Console.WriteLine($"* Hazards: {options.HazardCount:N0}");
        Console.WriteLine($"* Queries: {options.QueryCount:N0}");
        Console.WriteLine($"* Rounds: {options.Rounds:N0}");
        Console.WriteLine($"* H3 sparse risk grid: enabled");
        Console.WriteLine("----------------------------------------------------------");

        var random = new Random(options.Seed);
        var hazards = GenerateCityHazards(options, random);
        var queryPoints = GenerateCityQueries(options, random);

        var spatialIndex = new HazardSpatialIndex();
        var rebuildWatch = Stopwatch.StartNew();
        spatialIndex.Rebuild(hazards);
        rebuildWatch.Stop();

        var h3Grid = new H3HazardRiskGrid();
        var h3RebuildWatch = Stopwatch.StartNew();
        h3Grid.Rebuild(spatialIndex);
        h3RebuildWatch.Stop();

        var totalQueries = checked(options.QueryCount * options.Rounds);
        var batchSize = Math.Clamp(options.BatchSize, 1, totalQueries);
        var gridLatencies = new double[GetBatchCount(totalQueries, batchSize)];
        var rTreeSampleCount = Math.Min(options.QueryCount, options.RTreeSampleCount);
        var rTreeBatchSize = Math.Clamp(options.BatchSize, 1, Math.Max(1, rTreeSampleCount));
        var rTreeLatencies = new double[GetBatchCount(rTreeSampleCount, rTreeBatchSize)];
        var distanceLatencies = new double[GetBatchCount(totalQueries, batchSize)];

        WarmupCityBenchmark(h3Grid, spatialIndex, queryPoints, Math.Min(10_000, queryPoints.Count));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var gridAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var gridStopwatch = Stopwatch.StartNew();
        var riskSum = 0.0;
        var gridIndex = 0;
        for (var batchStart = 0; batchStart < totalQueries; batchStart += batchSize)
        {
            var batchEnd = Math.Min(totalQueries, batchStart + batchSize);
            var started = Stopwatch.GetTimestamp();
            for (var i = batchStart; i < batchEnd; i++)
            {
                var point = queryPoints[i % queryPoints.Count];
                riskSum += h3Grid.GetRisk(point.Lat, point.Lon);
            }

            gridLatencies[gridIndex++] = (Stopwatch.GetTimestamp() - started) / (double)Math.Max(1, batchEnd - batchStart);
        }
        gridStopwatch.Stop();
        var gridAllocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        var rTreeAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var rTreeStopwatch = Stopwatch.StartNew();
        var rTreeRiskSum = 0.0;
        var rTreeIndex = 0;
        for (var batchStart = 0; batchStart < rTreeSampleCount; batchStart += rTreeBatchSize)
        {
            var batchEnd = Math.Min(rTreeSampleCount, batchStart + rTreeBatchSize);
            var started = Stopwatch.GetTimestamp();
            for (var i = batchStart; i < batchEnd; i++)
            {
                var point = queryPoints[i];
                var nearby = spatialIndex.QueryNearby(point.Lat, point.Lon, 300);
                rTreeRiskSum += nearby.Count;
            }

            rTreeLatencies[rTreeIndex++] = (Stopwatch.GetTimestamp() - started) / (double)Math.Max(1, batchEnd - batchStart);
        }
        rTreeStopwatch.Stop();
        var rTreeAllocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        var distanceAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var distanceStopwatch = Stopwatch.StartNew();
        var distanceSum = 0.0;
        var distanceIndex = 0;
        for (var batchStart = 0; batchStart < totalQueries; batchStart += batchSize)
        {
            var batchEnd = Math.Min(totalQueries, batchStart + batchSize);
            var started = Stopwatch.GetTimestamp();
            for (var i = batchStart; i < batchEnd; i++)
            {
                var a = queryPoints[i % queryPoints.Count];
                var b = queryPoints[(i + 97) % queryPoints.Count];
                distanceSum += RiskScoringService.EquirectangularDistance(a.Lat, a.Lon, b.Lat, b.Lon);
            }

            distanceLatencies[distanceIndex++] = (Stopwatch.GetTimestamp() - started) / (double)Math.Max(1, batchEnd - batchStart);
        }
        distanceStopwatch.Stop();
        var distanceAllocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        var gridStats = LatencyStats.FromTicks(gridLatencies);
        var rTreeStats = LatencyStats.FromTicks(rTreeLatencies);
        var distanceStats = LatencyStats.FromTicks(distanceLatencies);
        var report = new CityBenchmarkReport(
            "accesscity-city-benchmark-v1",
            DateTime.UtcNow,
            options,
            new CityBenchmarkSummary(
                hazards.Count,
                totalQueries,
                Math.Round(rebuildWatch.Elapsed.TotalMilliseconds, 3),
                Math.Round(h3RebuildWatch.Elapsed.TotalMilliseconds, 3),
                Math.Round(totalQueries / gridStopwatch.Elapsed.TotalSeconds, 0),
                Math.Round(rTreeSampleCount / rTreeStopwatch.Elapsed.TotalSeconds, 0),
                Math.Round(totalQueries / distanceStopwatch.Elapsed.TotalSeconds, 0),
                Math.Round((gridAllocatedAfter - gridAllocatedBefore) / (double)Math.Max(1, totalQueries), 2),
                Math.Round((rTreeAllocatedAfter - rTreeAllocatedBefore) / (double)Math.Max(1, rTreeSampleCount), 2),
                Math.Round((distanceAllocatedAfter - distanceAllocatedBefore) / (double)Math.Max(1, totalQueries), 2),
                batchSize,
                gridLatencies.Length,
                riskSum,
                rTreeRiskSum,
                distanceSum),
            gridStats,
            rTreeStats,
            distanceStats,
            EvaluateGates(options, gridStats, distanceStats));

        var artifactDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "accesscity-city-benchmark"));
        Directory.CreateDirectory(artifactDir);
        var jsonPath = Path.Combine(artifactDir, "city_benchmark_report.json");
        var mdPath = Path.Combine(artifactDir, "city_benchmark_report.md");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(mdPath, BuildCityBenchmarkMarkdown(report));

        Console.WriteLine($"* H3 grid p50/p95/p99: {gridStats.P50Microseconds:F3}/{gridStats.P95Microseconds:F3}/{gridStats.P99Microseconds:F3} us");
        Console.WriteLine($"* H3 throughput: {report.Summary.H3RiskLookupOpsPerSecond:N0} ops/s");
        Console.WriteLine($"* R-tree sample p95: {rTreeStats.P95Microseconds:F3} us");
        Console.WriteLine($"* Distance p95: {distanceStats.P95Microseconds:F3} us");
        Console.WriteLine($"* Report: {jsonPath}");

        if (!report.Gates.Passed)
        {
            Console.Error.WriteLine($"City benchmark gate failed: {string.Join("; ", report.Gates.Failures)}");
            Environment.ExitCode = 1;
        }
    }

    private static List<HazardReport> GenerateCityHazards(CityBenchmarkOptions options, Random random)
    {
        var hazardTypes = new[]
        {
            "broken_sidewalk", "missing_curb_ramp", "blocked_pavement", "construction",
            "flooding", "stairs", "unsafe_crossing", "narrow_sidewalk"
        };
        var hazards = new List<HazardReport>(options.HazardCount);
        for (var i = 0; i < options.HazardCount; i++)
        {
            var lat = options.MinLat + random.NextDouble() * (options.MaxLat - options.MinLat);
            var lon = options.MinLon + random.NextDouble() * (options.MaxLon - options.MinLon);
            hazards.Add(new HazardReport
            {
                Id = Guid.NewGuid(),
                Location = new Point(lon, lat) { SRID = 4326 },
                Type = hazardTypes[i % hazardTypes.Length],
                Status = HazardStatus.Reported,
                ReportedAt = DateTime.UtcNow
            });
        }

        return hazards;
    }

    private static List<(double Lat, double Lon)> GenerateCityQueries(CityBenchmarkOptions options, Random random)
    {
        var points = new List<(double Lat, double Lon)>(options.QueryCount);
        for (var i = 0; i < options.QueryCount; i++)
        {
            points.Add((
                options.MinLat + random.NextDouble() * (options.MaxLat - options.MinLat),
                options.MinLon + random.NextDouble() * (options.MaxLon - options.MinLon)));
        }

        return points;
    }

    private static int GetBatchCount(int itemCount, int batchSize) =>
        Math.Max(1, (int)Math.Ceiling(itemCount / (double)Math.Max(1, batchSize)));

    private static void WarmupCityBenchmark(
        IHazardRiskGrid h3Grid,
        IHazardSpatialIndex spatialIndex,
        IReadOnlyList<(double Lat, double Lon)> queryPoints,
        int warmupCount)
    {
        var checksum = 0.0;
        for (var i = 0; i < warmupCount; i++)
        {
            var point = queryPoints[i % queryPoints.Count];
            checksum += h3Grid.GetRisk(point.Lat, point.Lon);
            if (i % 256 == 0)
            {
                checksum += spatialIndex.QueryNearby(point.Lat, point.Lon, 300).Count;
            }
        }

        if (checksum < 0)
        {
            throw new InvalidOperationException("Unreachable warmup checksum guard.");
        }
    }

    private static CityBenchmarkGates EvaluateGates(
        CityBenchmarkOptions options,
        LatencyStats gridStats,
        LatencyStats distanceStats)
    {
        var failures = new List<string>();
        if (gridStats.P99Microseconds > options.MaxGridP99Microseconds)
        {
            failures.Add($"H3 grid p99 {gridStats.P99Microseconds:F3}us > {options.MaxGridP99Microseconds:F3}us");
        }

        if (distanceStats.P99Microseconds > options.MaxDistanceP99Microseconds)
        {
            failures.Add($"distance p99 {distanceStats.P99Microseconds:F3}us > {options.MaxDistanceP99Microseconds:F3}us");
        }

        return new CityBenchmarkGates(failures.Count == 0, failures);
    }

    private static string BuildCityBenchmarkMarkdown(CityBenchmarkReport report) => $@"# AccessCity City-Scale Low-Latency Benchmark

## Scale
- Hazards: {report.Summary.HazardCount:N0}
- Queries: {report.Summary.QueryCount:N0}
- Spatial index rebuild: {report.Summary.SpatialIndexRebuildMilliseconds:F3} ms
- H3 risk grid rebuild: {report.Summary.H3GridRebuildMilliseconds:F3} ms

## Hot Path Latency
| Path | p50 | p95 | p99 | max |
| :--- | ---: | ---: | ---: | ---: |
| H3 risk lookup | {report.H3RiskLookup.P50Microseconds:F3} us | {report.H3RiskLookup.P95Microseconds:F3} us | {report.H3RiskLookup.P99Microseconds:F3} us | {report.H3RiskLookup.MaxMicroseconds:F3} us |
| R-tree nearby sample | {report.RTreeNearbyQuery.P50Microseconds:F3} us | {report.RTreeNearbyQuery.P95Microseconds:F3} us | {report.RTreeNearbyQuery.P99Microseconds:F3} us | {report.RTreeNearbyQuery.MaxMicroseconds:F3} us |
| Equirectangular distance | {report.DistanceKernel.P50Microseconds:F3} us | {report.DistanceKernel.P95Microseconds:F3} us | {report.DistanceKernel.P99Microseconds:F3} us | {report.DistanceKernel.MaxMicroseconds:F3} us |

## Throughput
- H3 risk lookup: {report.Summary.H3RiskLookupOpsPerSecond:N0} ops/s
- R-tree nearby sample: {report.Summary.RTreeNearbyOpsPerSecond:N0} ops/s
- Distance kernel: {report.Summary.DistanceKernelOpsPerSecond:N0} ops/s

## Allocation
- H3 risk lookup: {report.Summary.H3AllocatedBytesPerLookup:F2} bytes/op
- R-tree nearby sample: {report.Summary.RTreeAllocatedBytesPerQuery:F2} bytes/op
- Distance kernel: {report.Summary.DistanceAllocatedBytesPerCall:F2} bytes/op

## Gate
- Status: {(report.Gates.Passed ? "passed" : "failed")}
- Failures: {(report.Gates.Failures.Count == 0 ? "none" : string.Join("; ", report.Gates.Failures))}
";

    private sealed record CityBenchmarkOptions(
        int HazardCount,
        int QueryCount,
        int Rounds,
        int RTreeSampleCount,
        int Seed,
        double MinLat,
        double MinLon,
        double MaxLat,
        double MaxLon,
        int BatchSize,
        double MaxGridP99Microseconds,
        double MaxDistanceP99Microseconds)
    {
        public static CityBenchmarkOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    continue;
                }

                values[args[i][2..]] = args[++i];
            }

            return new CityBenchmarkOptions(
                GetInt(values, "hazards", 50_000),
                GetInt(values, "queries", 250_000),
                GetInt(values, "rounds", 1),
                GetInt(values, "rtree-samples", 10_000),
                GetInt(values, "seed", 42),
                GetDouble(values, "min-lat", 52.38),
                GetDouble(values, "min-lon", -2.02),
                GetDouble(values, "max-lat", 52.60),
                GetDouble(values, "max-lon", -1.72),
                GetInt(values, "batch-size", 256),
                GetDouble(values, "max-grid-p99-us", 25.0),
                GetDouble(values, "max-distance-p99-us", 10.0));
        }

        private static int GetInt(Dictionary<string, string> values, string key, int fallback) =>
            values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

        private static double GetDouble(Dictionary<string, string> values, string key, double fallback) =>
            values.TryGetValue(key, out var value) && double.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private sealed record LatencyStats(
        double P50Microseconds,
        double P95Microseconds,
        double P99Microseconds,
        double MaxMicroseconds)
    {
        public static LatencyStats FromTicks(double[] ticks)
        {
            Array.Sort(ticks);
            return new LatencyStats(
                ToMicroseconds(Percentile(ticks, 0.50)),
                ToMicroseconds(Percentile(ticks, 0.95)),
                ToMicroseconds(Percentile(ticks, 0.99)),
                ToMicroseconds(ticks.Length == 0 ? 0 : ticks[^1]));
        }

        private static double Percentile(double[] sortedTicks, double percentile)
        {
            if (sortedTicks.Length == 0) return 0;
            var index = Math.Clamp((int)Math.Ceiling(sortedTicks.Length * percentile) - 1, 0, sortedTicks.Length - 1);
            return sortedTicks[index];
        }

        private static double ToMicroseconds(double ticks) =>
            Math.Round(ticks * 1_000_000.0 / Stopwatch.Frequency, 4);
    }

    private sealed record CityBenchmarkSummary(
        int HazardCount,
        int QueryCount,
        double SpatialIndexRebuildMilliseconds,
        double H3GridRebuildMilliseconds,
        double H3RiskLookupOpsPerSecond,
        double RTreeNearbyOpsPerSecond,
        double DistanceKernelOpsPerSecond,
        double H3AllocatedBytesPerLookup,
        double RTreeAllocatedBytesPerQuery,
        double DistanceAllocatedBytesPerCall,
        int BatchSize,
        int LatencySampleCount,
        double RiskChecksum,
        double RTreeChecksum,
        double DistanceChecksum);

    private sealed record CityBenchmarkGates(bool Passed, IReadOnlyList<string> Failures);

    private sealed record CityBenchmarkReport(
        string HarnessVersion,
        DateTime GeneratedAtUtc,
        CityBenchmarkOptions Options,
        CityBenchmarkSummary Summary,
        LatencyStats H3RiskLookup,
        LatencyStats RTreeNearbyQuery,
        LatencyStats DistanceKernel,
        CityBenchmarkGates Gates);
}
