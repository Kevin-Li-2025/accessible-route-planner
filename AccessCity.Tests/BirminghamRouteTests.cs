using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Models;
using Xunit;
using Xunit.Abstractions;

namespace AccessCity.Tests;

/// <summary>
/// Birmingham-specific route tests that verify the routing algorithm
/// returns realistic distances and times for known city-centre routes.
/// Ground truth distances are from Google Maps walking directions.
/// </summary>
public class BirminghamRouteTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;
    private readonly ITestOutputHelper _output;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new NetTopologySuite.IO.Converters.GeoJsonConverterFactory() }
    };

    public BirminghamRouteTests(AccessCityApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    /// <summary>Moor Street Station → New Street Station (~500m, ~6 min).</summary>
    [Fact]
    public async Task Route_MoorSt_To_NewSt()
    {
        var result = await RunRoute(-1.8926, 52.4789, -1.8989, 52.4777);
        _output.WriteLine($"Moor St → New St: {result.Distance:F0}m, {result.EstimatedTime:F1} min, safety {result.SafetyScore:P0}");
        Assert.InRange(result.Distance, 200, 1500);       // Ground truth ~500m
        Assert.InRange(result.EstimatedTime, 2, 20);       // Ground truth ~6 min
        Assert.InRange(result.SafetyScore, 0, 1);
    }

    /// <summary>Bullring Shopping Centre → New Street Station (~400m, ~5 min).</summary>
    [Fact]
    public async Task Route_Bullring_To_NewSt()
    {
        var result = await RunRoute(-1.8930, 52.4776, -1.8989, 52.4777);
        _output.WriteLine($"Bullring → New St: {result.Distance:F0}m, {result.EstimatedTime:F1} min, safety {result.SafetyScore:P0}");
        Assert.InRange(result.Distance, 150, 1200);       // Ground truth ~400m
        Assert.InRange(result.EstimatedTime, 1, 15);
        Assert.InRange(result.SafetyScore, 0, 1);
    }

    /// <summary>Aston University → Birmingham City Centre (~1.2km, ~15 min).</summary>
    [Fact]
    public async Task Route_AstonUni_To_CityCentre()
    {
        var result = await RunRoute(-1.8900, 52.4870, -1.9000, 52.4780);
        _output.WriteLine($"Aston Uni → City Centre: {result.Distance:F0}m, {result.EstimatedTime:F1} min, safety {result.SafetyScore:P0}");
        Assert.InRange(result.Distance, 800, 4000);       // Ground truth ~1200m
        Assert.InRange(result.EstimatedTime, 8, 50);
        Assert.InRange(result.SafetyScore, 0, 1);
    }

    /// <summary>Five Ways → Broad Street (~500m, ~6 min).</summary>
    [Fact]
    public async Task Route_FiveWays_To_BroadSt()
    {
        var result = await RunRoute(-1.9130, 52.4740, -1.9100, 52.4780);
        _output.WriteLine($"Five Ways → Broad St: {result.Distance:F0}m, {result.EstimatedTime:F1} min, safety {result.SafetyScore:P0}");
        Assert.InRange(result.Distance, 200, 1600);       // Ground truth ~500m
        Assert.InRange(result.EstimatedTime, 2, 20);
        Assert.InRange(result.SafetyScore, 0, 1);
    }

    /// <summary>New Street Station → Millennium Point (~1km, ~12 min).</summary>
    [Fact]
    public async Task Route_NewSt_To_MillenniumPoint()
    {
        var result = await RunRoute(-1.8989, 52.4777, -1.8842, 52.4834);
        _output.WriteLine($"New St → Millennium Pt: {result.Distance:F0}m, {result.EstimatedTime:F1} min, safety {result.SafetyScore:P0}");
        Assert.InRange(result.Distance, 600, 3000);       // Ground truth ~1000m
        Assert.InRange(result.EstimatedTime, 6, 30);
        Assert.InRange(result.SafetyScore, 0, 1);
    }

    private async Task<RouteResponse> RunRoute(double startLng, double startLat, double endLng, double endLat)
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var request = new
        {
            Start = new { X = startLng, Y = startLat },
            End = new { X = endLng, Y = endLat },
            SafetyWeight = 0.5,
            Profile = "standard"
        };

        var sw = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", request, JsonOptions);
        sw.Stop();

        Assert.True(response.IsSuccessStatusCode, $"API returned {response.StatusCode}");
        var result = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(result);

        _output.WriteLine($"  Response: {sw.ElapsedMilliseconds}ms");
        return result;
    }
}
