using AccessCity.API.Models;
using AccessCity.API.Services;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using NetTopologySuite.Geometries;

namespace AccessCity.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, BenchmarkConfig.Instance);
    }
}

public sealed class BenchmarkConfig : ManualConfig
{
    public static readonly IConfig Instance = new BenchmarkConfig();

    private BenchmarkConfig()
    {
        AddJob(Job.Default
            .WithId("release-jit")
            .WithWarmupCount(3)
            .WithIterationCount(8));
        AddColumn(StatisticColumn.P50);
        AddColumn(StatisticColumn.P95);
        AddColumn(StatisticColumn.P100);
        AddExporter(JsonExporter.FullCompressed);
        AddLogger(ConsoleLogger.Default);
        WithOrderer(new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest));
    }
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
public class RiskHotPathBenchmarks
{
    private const int QueryCount = 100_000;
    private readonly List<(double Lat, double Lon)> _queries = new(QueryCount);
    private H3HazardRiskGrid _grid = null!;
    private double _checksum;

    [Params(50_000)]
    public int HazardCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        var hazards = new List<HazardReport>(HazardCount);
        for (var i = 0; i < HazardCount; i++)
        {
            hazards.Add(new HazardReport
            {
                Id = Guid.NewGuid(),
                Location = new Point(
                    -2.02 + random.NextDouble() * 0.30,
                    52.38 + random.NextDouble() * 0.22)
                { SRID = 4326 },
                Type = (i % 4) switch
                {
                    0 => "missing_curb_ramp",
                    1 => "blocked_pavement",
                    2 => "construction",
                    _ => "pothole"
                },
                Status = HazardStatus.Reported
            });
        }

        for (var i = 0; i < QueryCount; i++)
        {
            _queries.Add((
                52.38 + random.NextDouble() * 0.22,
                -2.02 + random.NextDouble() * 0.30));
        }

        var spatialIndex = new HazardSpatialIndex();
        spatialIndex.Rebuild(hazards);
        _grid = new H3HazardRiskGrid();
        _grid.Rebuild(spatialIndex);
    }

    [Benchmark(OperationsPerInvoke = QueryCount)]
    public double H3DenseAcceleratedRiskLookup()
    {
        var sum = 0.0;
        foreach (var query in _queries)
        {
            sum += _grid.GetRisk(query.Lat, query.Lon);
        }

        _checksum = sum;
        return _checksum;
    }

    [Benchmark(OperationsPerInvoke = QueryCount)]
    public double EquirectangularDistanceKernel()
    {
        var sum = 0.0;
        for (var i = 0; i < _queries.Count; i++)
        {
            var a = _queries[i];
            var b = _queries[(i + 97) % _queries.Count];
            sum += RiskScoringService.EquirectangularDistance(a.Lat, a.Lon, b.Lat, b.Lon);
        }

        _checksum = sum;
        return _checksum;
    }
}
