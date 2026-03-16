using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace AccessCity.Tests
{
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
            // 1. Prepare dataset
            int count = 5000;
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

            // 2. Measure concurrent writes
            var sw = Stopwatch.StartNew();
            var writeTasks = hazards.Select(h => _spatialCache.UpdateHazardCacheAsync(h));
            await Task.WhenAll(writeTasks);
            sw.Stop();
            _output.WriteLine($"Concurrent write of {count} items: {sw.ElapsedMilliseconds}ms");

            // 3. Measure concurrent reads
            sw.Restart();
            var readTasks = Enumerable.Range(0, 100).Select(i => 
                _spatialCache.GetHazardsInBoundsAsync(new Envelope(0, 0.05, 0, 0.05))
            );
            var results = await Task.WhenAll(readTasks);
            sw.Stop();
            _output.WriteLine($"100 concurrent spatial queries: {sw.ElapsedMilliseconds}ms");

            // 4. Assert
            Assert.All(results, r => Assert.NotNull(r));
            _output.WriteLine($"Average query latency: {sw.ElapsedMilliseconds / 100.0}ms");
        }
    }
}
