using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Serialization;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Xunit;

namespace AccessCity.Tests;

public class RoutingTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true,
        Converters = { new CoordinateJsonConverter(), new NetTopologySuite.IO.Converters.GeoJsonConverterFactory() }
    };

    public RoutingTests(AccessCityApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void RouteRequest_Coordinates_RoundTrip_Without_NaN_Or_Z()
    {
        var request = JsonSerializer.Deserialize<RouteRequest>(
            """
            {
              "start": {"x": -1.8985, "y": 52.4814},
              "end": [-1.93, 52.451],
              "preferences": ["wheelchair"],
              "safetyWeight": 0.6,
              "profile": "manual-wheelchair"
            }
            """,
            JsonOptions);

        Assert.NotNull(request);
        Assert.Equal(-1.8985, request!.Start.X, precision: 4);
        Assert.Equal(52.451, request.End.Y, precision: 4);

        var json = JsonSerializer.Serialize(request, JsonOptions);
        Assert.Contains("\"x\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"z\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NaN", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoordinateJsonConverter_Accepts_GeoJson_Point()
    {
        var coordinate = JsonSerializer.Deserialize<Coordinate>(
            """
            {"type":"Point","coordinates":[-1.8985,52.4814]}
            """,
            JsonOptions);

        Assert.NotNull(coordinate);
        Assert.Equal(-1.8985, coordinate!.X, precision: 4);
        Assert.Equal(52.4814, coordinate.Y, precision: 4);
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

        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", request, JsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(result);
        Assert.NotNull(result!.Path);
        Assert.True(result.Distance > 0);
        Assert.NotEmpty(result.Steps);
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

        var response = await client.GetAsync("/api/v1/routing/risk-score?lat=52.4862&lng=-1.8904&radius=500");
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
            End = new { X = -1.9300, Y = 52.4510 },   // University of Birmingham
            Preferences = new List<string>(),
            SafetyWeight = 0.5
        };

        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", request, JsonOptions);
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
        HttpClient client = await _factory.CreateAuthenticatedClientAsync();

        // Route near known hazard area with high safety weight
        var request = new
        {
            Start = new { X = -1.8985, Y = 52.4814 },
            End = new { X = -1.9300, Y = 52.4510 },
            Preferences = new List<string>(),
            SafetyWeight = 1.0  // Maximum safety preference
        };

        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", request, JsonOptions);
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

        var response = await client.GetAsync($"/api/v1/routing/ai-risk-score?lat={lat}&lng={lng}&radius=200");
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
        var response = await client.GetAsync("/api/v1/routing/ai-risk-score?lat=999&lng=999");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SafePath_WithProfile_Returns_Valid_Route()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        var request = new
        {
            Start = new { X = -1.8904, Y = 52.4862 }, // Node 1001
            End = new { X = -1.8894, Y = 52.4862 }, // Node 1003
            Profile = "manual-wheelchair",
            Preferences = new List<string> { "avoid-stairs" }
        };

        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", request, JsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(result);
        Assert.NotNull(result.Path);
    }

    [Fact]
    public async Task RouteGraphRepository_Hydrates_Shard_From_Distributed_Snapshot()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var distributedCache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        var metrics = scope.ServiceProvider.GetRequiredService<AccessCityMetrics>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<RoutingOptions>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RouteGraphRepository>>();
        var start = new Coordinate(-1.8904, 52.4862);
        var end = new Coordinate(-1.8894, 52.4862);

        using var firstMemory = new MemoryCache(new MemoryCacheOptions());
        var firstRepository = new RouteGraphRepository(
            dbContext,
            firstMemory,
            distributedCache,
            metrics,
            options,
            logger);

        var first = await firstRepository.LoadGraphAsync(start, end);
        Assert.True(first.HasCoverage);

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.RouteEdges.ExecuteDeleteAsync();
        await dbContext.RouteNodes.ExecuteDeleteAsync();

        using var secondMemory = new MemoryCache(new MemoryCacheOptions());
        var secondRepository = new RouteGraphRepository(
            dbContext,
            secondMemory,
            distributedCache,
            metrics,
            options,
            logger);

        var second = await secondRepository.LoadGraphAsync(start, end);
        Assert.True(second.HasCoverage);
        Assert.Equal(first.LoadedEdgeCount, second.LoadedEdgeCount);
        Assert.Equal(first.Nodes.Count, second.Nodes.Count);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task RouteGraphRepository_Does_Not_Persist_Empty_Shard_Snapshots()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var distributedCache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        var metrics = scope.ServiceProvider.GetRequiredService<AccessCityMetrics>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<RoutingOptions>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RouteGraphRepository>>();
        var start = new Coordinate(-1.8904, 52.4862);
        var end = new Coordinate(-1.8894, 52.4862);

        await dbContext.RouteEdges.ExecuteDeleteAsync();
        await dbContext.RouteNodes.ExecuteDeleteAsync();

        using (var emptyMemory = new MemoryCache(new MemoryCacheOptions()))
        {
            var emptyRepository = new RouteGraphRepository(
                dbContext,
                emptyMemory,
                distributedCache,
                metrics,
                options,
                logger);

            var empty = await emptyRepository.LoadGraphAsync(start, end);
            Assert.False(empty.HasCoverage);
        }

        await _factory.ImportOsmAsync(client);

        using var loadedMemory = new MemoryCache(new MemoryCacheOptions());
        var loadedRepository = new RouteGraphRepository(
            dbContext,
            loadedMemory,
            distributedCache,
            metrics,
            options,
            logger);

        var loaded = await loadedRepository.LoadGraphAsync(start, end);
        Assert.True(loaded.HasCoverage);
    }

    [Fact]
    public async Task SafePathOptions_Returns_Recommended_And_Optional_Variants()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var request = new
        {
            Start = new { X = -1.8985, Y = 52.4814 },
            End = new { X = -1.9300, Y = 52.4510 },
            Preferences = new List<string>(),
            SafetyWeight = 0.5,
            Profile = "standard"
        };

        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path/options", request, JsonOptions);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<SafePathOptionsResponse>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.NotNull(envelope!.Recommended);
        Assert.NotNull(envelope.Recommended.Path);
        Assert.True(envelope.Recommended.Distance > 0);

        Assert.NotNull(envelope.Variants);
        foreach (var v in envelope.Variants)
        {
            Assert.False(string.IsNullOrWhiteSpace(v.Kind));
            Assert.False(string.IsNullOrWhiteSpace(v.Description));
            Assert.NotNull(v.Route.Path);
            Assert.True(v.Route.Distance > 0);
        }
    }

    [Fact]
    public async Task SafePathAsync_Dispatches_To_Route_Worker_When_Enabled()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DATABASE_URL"] = string.Empty,
                    ["Postgres:ConnectionString"] = _factory.ConnectionString,
                    ["Postgres:AutoMigrate"] = "true",
                    ["OsmImport:ImportOnStartup"] = "false",
                    ["Messaging:UseKafka"] = "false",
                    ["Routing:DispatchJobsToWorker"] = "true",
                    ["Workers:OsmImport:Enabled"] = "false",
                    ["Workers:Routing:Enabled"] = "true",
                    ["Workers:TileWarming:Enabled"] = "false"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.AddHostedService<AccessCity.API.Services.Background.RouteJobBackgroundService>();
            });
        });

        var client = factory.CreateClient();
        var request = new
        {
            Start = new { X = -1.8904, Y = 52.4862 },
            End = new { X = -1.8894, Y = 52.4862 },
            Preferences = new List<string>(),
            SafetyWeight = 0.5,
            Profile = "standard"
        };

        var accepted = await client.PostAsJsonAsync("/api/v1/routing/safe-path/async", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        using var payload = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var jobId = payload.RootElement.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jobId));

        RouteJobResult? final = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var poll = await client.GetAsync($"/api/v1/routing/jobs/{jobId}");
            poll.EnsureSuccessStatusCode();
            final = await poll.Content.ReadFromJsonAsync<RouteJobResult>(JsonOptions);

            if (final?.Status is RouteJobStatus.Completed or RouteJobStatus.Failed)
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.NotNull(final);
        Assert.Equal(RouteJobStatus.Completed, final!.Status);
        Assert.NotNull(final.Route);
        Assert.True(final.Route!.Distance > 0);
    }
}
