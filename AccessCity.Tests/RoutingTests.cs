using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;

namespace AccessCity.Tests;

public class RoutingTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true,
        Converters = { new NetTopologySuite.IO.Converters.GeoJsonConverterFactory() }
    };

    public RoutingTests(AccessCityApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetSafePath_Uses_Imported_Route_Graph()
    {
        var client = await _factory.CreateAuthenticatedClientAsync(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await _factory.ImportOsmAsync(client);

        var request = new
        {
            Start = new { X = -1.8904, Y = 52.4862 },
            End = new { X = -1.8894, Y = 52.4862 },
            Preferences = new List<string> { "prefer-crossings" },
            SafetyWeight = 0.4
        };

        var response = await client.PostAsJsonAsync("/api/routing/safe-path", request, JsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(result);
        Assert.NotNull(result!.Path);
        Assert.True(result.Distance > 0);
        Assert.NotEmpty(result.Steps);
    }

    [Fact]
    public async Task GetSafePath_Returns_Clear_Error_When_No_Graph_Is_Imported()
    {
        using var freshFactory = new AccessCityApiFactory();
        var client = await freshFactory.CreateAuthenticatedClientAsync(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new
        {
            Start = new { X = -1.8904, Y = 52.4862 },
            End = new { X = -1.8894, Y = 52.4862 },
            Preferences = new List<string>(),
            SafetyWeight = 0.5
        };

        var response = await client.PostAsJsonAsync("/api/routing/safe-path", request, JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Import OSM data", content);
    }

    [Fact]
    public async Task GetRiskScore_Returns_Score_From_Persisted_Hazards()
    {
        var client = await _factory.CreateAuthenticatedClientAsync(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Hazards.Add(new HazardReport
            {
                Id = Guid.NewGuid(),
                Location = new Point(-1.8904, 52.4862) { SRID = 4326 },
                Type = "pothole",
                Description = "DB-backed hazard for risk scoring",
                PhotoUrl = "https://example.com/hazard.jpg",
                Status = HazardStatus.Reported,
                ReportedAt = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/routing/risk-score?lat=52.4862&lng=-1.8904&radius=500");
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RiskScoreResponse>(JsonOptions);
        Assert.NotNull(result);
        Assert.True(result!.OverallRisk >= 0);
        Assert.True(result.NearbyHazardCount >= 1);
    }
}
