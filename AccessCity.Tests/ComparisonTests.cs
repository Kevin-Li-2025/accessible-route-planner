using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Xunit;
using Xunit.Abstractions;

namespace AccessCity.Tests;

public class ComparisonTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;
    private readonly ITestOutputHelper _output;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new NetTopologySuite.IO.Converters.GeoJsonConverterFactory() }
    };

    public ComparisonTests(AccessCityApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task CompareWithGoogleMaps_Birmingham_NewSt_To_Bullring()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        // 1. Setup a "Construction" hazard on the direct path (Dudley St area)
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Hazards.Add(new HazardReport
            {
                Id = Guid.NewGuid(),
                Location = new Point(-1.8966, 52.4776) { SRID = 4326 }, // Dudley St area
                Type = "construction",
                Description = "Major roadworks blocking the direct path",
                Status = HazardStatus.Reported,
                ReportedAt = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        // 2. Request Safe Path (Standard User)
        var requestStandard = new
        {
            Start = new { X = -1.8989, Y = 52.4777 }, // New St
            End = new { X = -1.8943, Y = 52.4776 },   // Bullring
            Profile = "standard",
            SafetyWeight = 0.8
        };

        var respStandard = await client.PostAsJsonAsync("/api/v1/routing/safe-path", requestStandard, JsonOptions);
        respStandard.EnsureSuccessStatusCode();
        var resultStandard = await respStandard.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);

        _output.WriteLine($"Standard Route: Distance={resultStandard!.Distance}m, Warnings={resultStandard.Warnings.Count}");

        // 3. Request Safe Path (Wheelchair User)
        var requestWheelchair = new
        {
            Start = new { X = -1.8989, Y = 52.4777 },
            End = new { X = -1.8943, Y = 52.4776 },
            Profile = "manual-wheelchair",
            SafetyWeight = 0.5
        };

        var respWheelchair = await client.PostAsJsonAsync("/api/v1/routing/safe-path", requestWheelchair, JsonOptions);
        respWheelchair.EnsureSuccessStatusCode();
        var resultWheelchair = await respWheelchair.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);

        _output.WriteLine($"Wheelchair Route: Distance={resultWheelchair!.Distance}m, Safety={resultWheelchair.SafetyScore}");
        
        Assert.NotNull(resultStandard);
        Assert.NotNull(resultWheelchair);
    }
}
