using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace AccessCity.Tests;

public class SpatialCachePerformanceTests : IClassFixture<AccessCityApiFactory>
{
    private readonly ISpatialCacheService _spatialCache;
    private readonly ITestOutputHelper _output;

    public SpatialCachePerformanceTests(AccessCityApiFactory factory, ITestOutputHelper output)
    {
        var scope = factory.Services.CreateScope();
        _spatialCache = scope.ServiceProvider.GetRequiredService<ISpatialCacheService>();
        _output = output;
    }

    [Fact]
    public async Task Benchmark_Concurrent_Spatial_Operations()
    {
        const int count = 5000;
        var hazards = Enumerable.Range(0, count).Select(i => new HazardReport
        {
            Id = Guid.NewGuid(),
            Location = new Point(i * 0.0001, i * 0.0001),
            Type = "benchmark",
            Status = HazardStatus.Reported,
            Description = "performance test",
            PhotoUrl = "url"
        }).ToList();

        _output.WriteLine($"Starting benchmark with {count} items...");

        var sw = Stopwatch.StartNew();
        var writeTasks = hazards.Select(h => _spatialCache.UpdateHazardCacheAsync(h));
        await Task.WhenAll(writeTasks);
        sw.Stop();
        _output.WriteLine($"Concurrent write of {count} items: {sw.ElapsedMilliseconds}ms");

        sw.Restart();
        var readTasks = Enumerable.Range(0, 100).Select(_ =>
            _spatialCache.GetHazardsInBoundsAsync(new Envelope(0, 0.05, 0, 0.05)));
        var results = await Task.WhenAll(readTasks);
        sw.Stop();
        _output.WriteLine($"100 concurrent spatial queries: {sw.ElapsedMilliseconds}ms");

        Assert.All(results, r => Assert.NotNull(r));
        _output.WriteLine($"Average query latency: {sw.ElapsedMilliseconds / 100.0}ms");
    }

    /// <summary>
    /// Higher read fan-out after a warm cache: simulates many map tiles or clients querying the same bbox.
    /// </summary>
    [Fact]
    public async Task Stress_HighConcurrentReads_OverHotRegion()
    {
        const int seedCount = 2000;
        const int readParallelism = 320;
        var envelope = new Envelope(-0.02, 0.08, -0.02, 0.08);

        var seed = Enumerable.Range(0, seedCount).Select(i => new HazardReport
        {
            Id = Guid.NewGuid(),
            Location = new Point(-0.01 + i * 0.00002, -0.01 + i * 0.00002),
            Type = "stress",
            Status = HazardStatus.Reported,
            Description = "stress read",
            PhotoUrl = ""
        }).ToList();

        await Task.WhenAll(seed.Select(h => _spatialCache.UpdateHazardCacheAsync(h)));

        var sw = Stopwatch.StartNew();
        var readTasks = Enumerable.Range(0, readParallelism)
            .Select(_ => _spatialCache.GetHazardsInBoundsAsync(envelope));
        var results = await Task.WhenAll(readTasks);
        sw.Stop();

        Assert.All(results, r => Assert.NotNull(r));
        _output.WriteLine(
            $"{readParallelism} concurrent reads over bbox after {seedCount} inserts: {sw.ElapsedMilliseconds} ms total");
    }
}
