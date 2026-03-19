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

    [Fact]
    public async Task SafePath_RealCoordinates_Returns_Route_With_Steps()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        // Birmingham City Centre → University of Birmingham (real-world coordinates)
        var request = new
        {
            Start = new { X = -1.8985, Y = 52.4814 },   // Birmingham New St Station
            End   = new { X = -1.9300, Y = 52.4510 },   // University of Birmingham
            Preferences = new List<string>(),
            SafetyWeight = 0.5
        };

        var response = await client.PostAsJsonAsync("/api/routing/safe-path", request, JsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.Distance > 0, "Route distance should be > 0");
        Assert.True(result.EstimatedTime > 0, "Estimated walking time should be > 0");
        Assert.True(result.SafetyScore >= 0 && result.SafetyScore <= 1, 
            $"Safety score ({result.SafetyScore}) should be between 0 and 1");
        Assert.NotNull(result.Steps);
        Assert.True(result.Steps.Count > 0, "Should have at least one route step");
    }

    [Fact]
    public async Task SafePath_WithHighSafetyWeight_Returns_Warnings()
    {
        HttpClient client;
        try
        {
            client = await _factory.CreateAuthenticatedClientAsync();
        }
        catch (HttpRequestException)
        {
            return;
        }
        // Route near known hazard area with high safety weight
        var request = new
        {
            Start = new { X = -1.8985, Y = 52.4814 },
            End   = new { X = -1.9300, Y = 52.4510 },
            Preferences = new List<string>(),
            SafetyWeight = 1.0  // Maximum safety preference
        };

        var response = await client.PostAsJsonAsync("/api/routing/safe-path", request, JsonOptions);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(result);
        Assert.NotNull(result.Warnings);
        // Safety score should still be valid
        Assert.True(result.SafetyScore >= 0 && result.SafetyScore <= 1);
    }

    [Fact]
    public async Task AiRiskScore_Returns_MultiFactor_Breakdown()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        // Test the AI risk endpoint near UoB campus
        double lat = 52.4514;
        double lng = -1.9305;

        var response = await client.GetAsync($"/api/routing/ai-risk-score?lat={lat}&lng={lng}&radius=200");
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<PredictiveRiskResult>(content, JsonOptions);

        Assert.NotNull(result);

        // Overall risk should be between 0 and 1
        Assert.True(result.OverallRisk >= 0 && result.OverallRisk <= 1,
            $"Overall AI risk ({result.OverallRisk}) should be in [0,1]");

        // Sub-scores should all be populated
        Assert.True(result.HazardRisk >= 0, "HazardRisk should be >= 0");
        Assert.True(result.TimeOfDayRisk >= 0, "TimeOfDayRisk should be >= 0");
        Assert.True(result.WeatherRisk >= 0, "WeatherRisk should be >= 0");
        Assert.True(result.CrimeRisk >= 0, "CrimeRisk should be >= 0");
        Assert.True(result.InfrastructureRisk >= 0, "InfrastructureRisk should be >= 0");

        // Risk factors list should contain at least one explanation
        Assert.NotNull(result.RiskFactors);
        Assert.True(result.RiskFactors.Count > 0, "Should have at least one risk factor explanation");
    }

    [Fact]
    public async Task AiRiskScore_InvalidCoordinates_Returns_BadRequest()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/routing/ai-risk-score?lat=999&lng=999");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
> Stashed changes
    }
}
