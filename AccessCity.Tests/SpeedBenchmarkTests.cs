using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AccessCity.Tests;

public class SpeedBenchmarkTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;
    private readonly ITestOutputHelper _output;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new NetTopologySuite.IO.Converters.GeoJsonConverterFactory() }
    };

    public SpeedBenchmarkTests(AccessCityApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task Benchmark_Routing_Speed_Cold_vs_Warm()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var request = new { 
            Start = new { X = -1.8985, Y = 52.4814 }, 
            End = new { X = -1.9300, Y = 52.4510 }, 
            SafetyWeight = 0.5 
        };

        _output.WriteLine("### Performance Analysis: Environmental Data Integration");

        // 1. Cold Start (Triggers Overpass queries)
        var sw = Stopwatch.StartNew();
        var resp1 = await client.PostAsJsonAsync("/api/v1/routing/safe-path", request, JsonOptions);
        sw.Stop();
        _output.WriteLine($"- **Cold Start (First Request)**: {sw.ElapsedMilliseconds}ms (Includes fetching OSM/Crime/Weather)");

        // 2. Warm Start (Cache Hits)
        sw.Restart();
        var resp2 = await client.PostAsJsonAsync("/api/v1/routing/safe-path", request, JsonOptions);
        sw.Stop();
        _output.WriteLine($"- **Warm Start (Cached)**: {sw.ElapsedMilliseconds}ms (O(1) risk lookups)");

        Assert.True(resp1.IsSuccessStatusCode);
        Assert.True(resp2.IsSuccessStatusCode);
    }
}
