using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Serialization;
using AccessCity.API.Services;
using AccessCity.API.Services.External;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
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

    private static HazardReport BuildFingerprintHazard(string type) => new()
    {
        Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Location = new Point(-1.8904, 52.4862) { SRID = 4326 },
        Type = type,
        Description = "fingerprint hazard",
        ReportedAt = new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc),
        Status = HazardStatus.Reported
    };

    [Fact]
    public void RouteRequestFingerprint_Changes_For_Preferences_And_Hazards()
    {
        var prefsA = RouteRequestFingerprint.CanonicalPreferences(new[] { "prefer-crossings", "avoid-stairs" });
        var prefsB = RouteRequestFingerprint.CanonicalPreferences(new[] { "avoid-stairs", "prefer-crossings" });
        var prefsC = RouteRequestFingerprint.CanonicalPreferences(new[] { "low-light-penalty" });

        Assert.Equal(prefsA, prefsB);
        Assert.NotEqual(prefsA, prefsC);

        var hazard = new HazardReport
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Location = new Point(-1.8904, 52.4862) { SRID = 4326 },
            Type = "pothole",
            Description = "fingerprint hazard",
            ReportedAt = new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc),
            Status = HazardStatus.Reported
        };

        var activeContext = RouteRequestFingerprint.HazardContext(new[] { hazard });
        hazard.Status = HazardStatus.Resolved;
        var resolvedContext = RouteRequestFingerprint.HazardContext(new[] { hazard });

        Assert.NotEqual(activeContext, resolvedContext);
    }

    [Fact]
    public void RouteRequestFingerprint_Normalizes_Equivalent_Hazard_Types()
    {
        var hazardA = BuildFingerprintHazard("blocked pavement");
        var hazardB = BuildFingerprintHazard("blocked-pavement");
        var hazardC = BuildFingerprintHazard("pothole");

        var contextA = RouteRequestFingerprint.HazardContext(new[] { hazardA });
        var contextB = RouteRequestFingerprint.HazardContext(new[] { hazardB });
        var contextC = RouteRequestFingerprint.HazardContext(new[] { hazardC });

        Assert.Equal(contextA, contextB);
        Assert.NotEqual(contextA, contextC);
    }

    [Fact]
    public async Task RouteOptionsCoalescing_Shares_InFlight_Options_Computation()
    {
        var coalescing = new RouteCoalescingService(
            NullLogger<RouteCoalescingService>.Instance,
            new AccessCityMetrics());
        var request = new RouteRequest
        {
            Start = new Coordinate(-1.8904, 52.4862),
            End = new Coordinate(-1.8894, 52.4862),
            Profile = "manual-wheelchair",
            SafetyWeight = 0.7,
            Preferences = new List<string> { "wheelchair", "prefer-crossings" }
        };
        var factoryEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var computeCount = 0;

        var tasks = Enumerable
            .Range(0, 24)
            .Select(_ => coalescing.GetOrComputeOptionsAsync(
                request,
                "hazards:test:graph:test",
                async () =>
                {
                    Interlocked.Increment(ref computeCount);
                    factoryEntered.TrySetResult(true);
                    await releaseFactory.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return new SafePathOptionsResponse
                    {
                        Recommended = new RouteResponse
                        {
                            Path = new LineString(new[] { request.Start, request.End }),
                            Distance = 123,
                            EstimatedTime = 98,
                            SafetyScore = 0.82
                        },
                        Variants = new List<RoutedOptionVariant>()
                    };
                }))
            .ToArray();

        await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFactory.SetResult(true);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, Volatile.Read(ref computeCount));
        Assert.All(results, result =>
        {
            Assert.NotNull(result);
            Assert.Equal(123, result!.Recommended.Distance);
            Assert.Equal(0.82, result.Recommended.SafetyScore);
        });
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
        Assert.DoesNotContain(
            result.Warnings,
            warning => warning.Contains("approximate mesh", StringComparison.OrdinalIgnoreCase));
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
            scope.ServiceProvider.GetRequiredService<IHazardSpatialIndex>().MarkStale();
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
    public async Task SafePath_Relaxes_Accessibility_Filters_On_Real_Graph_Before_Osrm()
    {
        var start = new Coordinate(-1.8904, 52.4862);
        var end = new Coordinate(-1.8894, 52.4862);
        var graph = new RouteGraphData
        {
            Nodes = new Dictionary<long, GraphNode>
            {
                [1] = new()
                {
                    Id = 1,
                    Location = start,
                    Edges =
                    {
                        [2] = BuildTestEdge(end, distanceMetres: 80, hasStairs: true)
                    }
                },
                [2] = new()
                {
                    Id = 2,
                    Location = end
                }
            },
            LoadedEdgeCount = 1
        };

        var risk = new Mock<IRiskScoringService>();
        risk.Setup(x => x.QuickRisk(
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<IEnumerable<HazardReport>>(),
                It.IsAny<double>()))
            .Returns(0);

        var aiRisk = new Mock<IPredictiveRiskModel>();
        var osrm = new Mock<IOsrmClient>();
        osrm.Setup(x => x.GetAlternativeRoutesAsync(
                It.IsAny<Coordinate>(),
                It.IsAny<Coordinate>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OsrmRouteResult>());

        var routeGraph = new Mock<IRouteGraphRepository>();
        routeGraph.Setup(x => x.LoadGraphAsync(
                It.IsAny<Coordinate>(),
                It.IsAny<Coordinate>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(graph);

        var routeGraphStatus = new Mock<IRouteGraphStatusService>();
        routeGraphStatus.Setup(x => x.GetVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-graph");
        routeGraphStatus.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouteGraphCoverageStatus(2, 1, true, "test-graph", null, null, null, null, null));

        var routeCache = new Mock<IRouteCacheService>();
        routeCache.Setup(x => x.TryGetAsync(It.IsAny<string>()))
            .ReturnsAsync((RouteResponse?)null);
        routeCache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<RouteResponse>()))
            .Returns(Task.CompletedTask);
        routeCache.Setup(x => x.BuildKey(
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<string>(),
                It.IsAny<double>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<string?>()))
            .Returns("route:test");

        var tileCache = new Mock<IRiskTileCacheService>();
        var hazardGrid = new Mock<IHazardRiskGrid>();
        var hazardIndex = new Mock<IHazardSpatialIndex>();
        var service = new RoutingService(
            risk.Object,
            aiRisk.Object,
            osrm.Object,
            routeGraph.Object,
            routeGraphStatus.Object,
            tileCache.Object,
            routeCache.Object,
            hazardGrid.Object,
            hazardIndex.Object,
            Options.Create(new RoutingOptions { RouteGraphMaxSnapDistanceMetres = 150 }));

        var request = new RouteRequest
        {
            Start = start,
            End = end,
            Profile = "manual-wheelchair",
            Preferences = new List<string> { "avoid-stairs" },
            SafetyWeight = 0.8
        };

        var response = await service.FindSafePathAsync(request, Enumerable.Empty<HazardReport>());

        Assert.NotNull(response.Path);
        Assert.Equal(80, response.Distance, precision: 1);
        Assert.Contains(response.Warnings, warning =>
            warning.Contains("No fully verified accessible path", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(response.Warnings, warning =>
            warning.Contains("contains stairs", StringComparison.OrdinalIgnoreCase));
        osrm.Verify(x => x.GetAlternativeRoutesAsync(
                It.IsAny<Coordinate>(),
                It.IsAny<Coordinate>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SafePath_Does_Not_Snap_To_Distant_RouteGraph_Fixture()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        var request = new
        {
            Start = new { X = -1.89, Y = 52.48 },
            End = new { X = -1.88, Y = 52.485 },
            Profile = "manual-wheelchair",
            Preferences = new List<string> { "avoid-reported-hazards", "prefer-crossings" },
            SafetyWeight = 0.75
        };

        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", request, JsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(result);
        Assert.NotNull(result!.Path);
        Assert.True(result.Distance > 500, $"Expected full route distance, got {result.Distance}m.");

        var first = result.Path!.Coordinates.First();
        var last = result.Path.Coordinates.Last();
        var startSnap = RiskScoringService.HaversineDistance(52.48, -1.89, first.Y, first.X);
        var endSnap = RiskScoringService.HaversineDistance(52.485, -1.88, last.Y, last.X);

        Assert.True(startSnap < 250, $"Route start was snapped {startSnap:F0}m away from the requested origin.");
        Assert.True(endSnap < 250, $"Route end was snapped {endSnap:F0}m away from the requested destination.");
    }

    [Fact]
    public async Task SafePath_WithAccessibilityProfile_Warns_When_Osm_Accessibility_Tags_Are_Missing()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        var request = new
        {
            Start = new { X = -1.8904, Y = 52.4862 }, // Node 1001
            End = new { X = -1.8899, Y = 52.48645 }, // Node 1004 via way 2002, which lacks width/smoothness
            Profile = "manual-wheelchair",
            Preferences = new List<string> { "wheelchair" },
            SafetyWeight = 0.6
        };

        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", request, JsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(result);
        Assert.Contains(result!.Warnings, warning =>
            warning.Contains("Accessibility data confidence is lower", StringComparison.OrdinalIgnoreCase)
            && warning.Contains("missing width", StringComparison.OrdinalIgnoreCase)
            && warning.Contains("missing smoothness", StringComparison.OrdinalIgnoreCase));
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
        var routeGraphStatus = scope.ServiceProvider.GetRequiredService<IRouteGraphStatusService>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<RoutingOptions>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RouteGraphRepository>>();
        var artifactStore = scope.ServiceProvider.GetRequiredService<IRouteGraphArtifactStore>();
        var start = new Coordinate(-1.8904, 52.4862);
        var end = new Coordinate(-1.8894, 52.4862);

        using var firstMemory = new MemoryCache(new MemoryCacheOptions());
        var firstRepository = new RouteGraphRepository(
            dbContext,
            firstMemory,
            distributedCache,
            metrics,
            routeGraphStatus,
            options,
            logger,
            artifactStore);

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
            routeGraphStatus,
            options,
            logger,
            artifactStore);

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
        var routeGraphStatus = scope.ServiceProvider.GetRequiredService<IRouteGraphStatusService>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<RoutingOptions>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RouteGraphRepository>>();
        var artifactStore = scope.ServiceProvider.GetRequiredService<IRouteGraphArtifactStore>();
        var start = new Coordinate(-1.8904, 52.4862);
        var end = new Coordinate(-1.8894, 52.4862);

        await dbContext.RouteEdges.ExecuteDeleteAsync();
        await dbContext.RouteNodes.ExecuteDeleteAsync();
        routeGraphStatus.InvalidateLocalCache();
        var emptyCacheKey = BuildRouteGraphCacheKey(
            start,
            end,
            options.Value,
            await routeGraphStatus.GetVersionAsync());

        await distributedCache.RemoveAsync(emptyCacheKey);

        using (var emptyMemory = new MemoryCache(new MemoryCacheOptions()))
        {
            var emptyRepository = new RouteGraphRepository(
                dbContext,
                emptyMemory,
                distributedCache,
                metrics,
                routeGraphStatus,
                options,
                logger,
                artifactStore);

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
            routeGraphStatus,
            options,
            logger,
            artifactStore);

        var loaded = await loadedRepository.LoadGraphAsync(start, end);
        Assert.True(loaded.HasCoverage);
    }

    [Fact]
    public async Task RouteGraphRepository_Loads_Prepartitioned_Packed_Shards()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var distributedCache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        var metrics = scope.ServiceProvider.GetRequiredService<AccessCityMetrics>();
        var routeGraphStatus = scope.ServiceProvider.GetRequiredService<IRouteGraphStatusService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RouteGraphRepository>>();
        var artifactStore = scope.ServiceProvider.GetRequiredService<IRouteGraphArtifactStore>();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = Options.Create(new RoutingOptions
        {
            RouteGraphPrepartitionedShardsEnabled = true,
            RouteGraphPackedArtifactsEnabled = true,
            RouteGraphAltPreprocessingEnabled = true,
            RouteGraphAltLandmarkCount = 2,
            RouteGraphShardSizeDegrees = 0.01,
            RouteGraphMaxPrepartitionedShardCount = 64,
            RouteGraphMinEdgesPerPrepartitionedShard = 25,
            MaxRouteGraphEdges = 20_000
        });

        var repository = new RouteGraphRepository(
            dbContext,
            memoryCache,
            distributedCache,
            metrics,
            routeGraphStatus,
            options,
            logger,
            artifactStore);

        var loaded = await repository.LoadGraphAsync(
            new Coordinate(-1.8904, 52.4862),
            new Coordinate(-1.8894, 52.4862));

        Assert.True(loaded.HasCoverage);
        Assert.Contains("bundle", loaded.ShardKey);
        Assert.True(loaded.SourceShardKeys.Count > 0);
        Assert.True(loaded.Preprocessing?.HasLandmarks);
        Assert.All(
            loaded.Nodes.Values.SelectMany(node => node.Edges.Values),
            edge => Assert.Equal(RouteEdgeCostModel.EdgeWeightVersion, edge.EdgeWeightVersion));
    }

    [Fact]
    public async Task RouteGraphRepository_Loads_File_Manifest_Artifact_Before_Postgis()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var distributedCache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        var metrics = scope.ServiceProvider.GetRequiredService<AccessCityMetrics>();
        var routeGraphStatus = scope.ServiceProvider.GetRequiredService<IRouteGraphStatusService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RouteGraphRepository>>();
        var start = new Coordinate(-1.8904, 52.4862);
        var end = new Coordinate(-1.8894, 52.4862);
        var artifactDirectory = Path.Combine(Path.GetTempPath(), $"accesscity-route-manifest-{Guid.NewGuid():N}");
        var options = Options.Create(new RoutingOptions
        {
            RouteGraphPackedArtifactsEnabled = true,
            RouteGraphFileArtifactStoreEnabled = true,
            RouteGraphFileArtifactDirectory = artifactDirectory,
            RouteGraphFileArtifactManifestEnabled = true,
            RouteGraphFileArtifactWriteThroughEnabled = false,
            RouteGraphPrepartitionedShardsEnabled = false,
            RouteGraphAltPreprocessingEnabled = true,
            RouteGraphAltLandmarkCount = 2,
            RouteGraphMaxAltPreprocessedNodes = 10_000,
            MaxRouteGraphEdges = 20_000
        });
        var artifactStore = new RouteGraphArtifactStore(
            options,
            NullLogger<RouteGraphArtifactStore>.Instance);

        using var firstMemory = new MemoryCache(new MemoryCacheOptions());
        var firstRepository = new RouteGraphRepository(
            dbContext,
            firstMemory,
            distributedCache,
            metrics,
            routeGraphStatus,
            options,
            logger,
            artifactStore);

        var first = await firstRepository.LoadGraphAsync(start, end);
        Assert.True(first.HasCoverage);
        Assert.False(string.IsNullOrWhiteSpace(first.ShardKey));

        var artifact = RouteGraphArtifactCodec.Pack(first);
        var payload = RouteGraphArtifactCodec.SerializeRedisPayload(artifact);
        var manifestShardCacheKey = $"{first.ShardKey}:offline-shard";
        var written = await artifactStore.WriteAsync(manifestShardCacheKey, artifact, payload, "unit-test-manifest");
        Assert.NotNull(written);

        var manifest = new RouteGraphArtifactManifest(
            RouteGraphArtifactCodec.SchemaVersion,
            RouteEdgeCostModel.Version,
            RouteEdgeCostModel.EdgeWeightVersion,
            RouteGraphPreprocessor.AltAlgorithmVersion,
            options.Value.RouteGraphShardSizeDegrees,
            "unit-test.osm",
            DateTime.UtcNow,
            new[]
            {
                new RouteGraphArtifactManifestShard(
                    "000-missing-route-graph-shard",
                    -1.92,
                    52.47,
                    -1.86,
                    52.50,
                    first.Nodes.Count,
                    first.LoadedEdgeCount,
                    1,
                    DateTime.UtcNow,
                    "unit-test-manifest",
                    "missing-route-graph-shard.acrg",
                    "missing"),
                new RouteGraphArtifactManifestShard(
                    manifestShardCacheKey,
                    -1.92,
                    52.47,
                    -1.86,
                    52.50,
                    first.Nodes.Count,
                    first.LoadedEdgeCount,
                    written!.PayloadBytes,
                    written.CreatedAtUtc,
                    "unit-test-manifest",
                    Path.GetFileName(written.ArtifactPath),
                    written.PayloadSha256)
            });
        Assert.NotNull(await artifactStore.WriteManifestAsync(manifest));
        await distributedCache.RemoveAsync(first.ShardKey!);
        await distributedCache.SetAsync(
            manifestShardCacheKey,
            payload,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        File.Delete(written!.ArtifactPath);
        File.Delete(Path.ChangeExtension(written.ArtifactPath, ".json"));

        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.RouteEdges.ExecuteDeleteAsync();
        await dbContext.RouteNodes.ExecuteDeleteAsync();
        routeGraphStatus.InvalidateLocalCache();

        using var secondMemory = new MemoryCache(new MemoryCacheOptions());
        var secondRepository = new RouteGraphRepository(
            dbContext,
            secondMemory,
            distributedCache,
            metrics,
            routeGraphStatus,
            options,
            logger,
            artifactStore);

        var second = await secondRepository.LoadGraphAsync(start, end);
        Assert.True(second.HasCoverage);
        Assert.Equal(first.LoadedEdgeCount, second.LoadedEdgeCount);
        Assert.Equal(first.Nodes.Count, second.Nodes.Count);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task RouteGraphRepository_Loads_Relevant_File_Manifest_Shards_When_Matches_Exceed_Limit()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"route_graph_manifest_limit_{Guid.NewGuid():N}")
            .Options;
        await using var dbContext = new AppDbContext(dbOptions);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var distributedCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var routeGraphStatus = new Mock<IRouteGraphStatusService>();
        routeGraphStatus
            .Setup(status => status.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RouteGraphCoverageStatus(
                0,
                0,
                false,
                "osm:empty:unit-test",
                null,
                null,
                null,
                null,
                "empty graph"));
        var routingOptions = Options.Create(new RoutingOptions
        {
            RouteGraphPackedArtifactsEnabled = true,
            RouteGraphFileArtifactStoreEnabled = true,
            RouteGraphFileArtifactManifestEnabled = true,
            RouteGraphFileArtifactWriteThroughEnabled = false,
            RouteGraphMaxFileArtifactShardLoadCount = 1,
            RouteGraphShardSizeDegrees = 0.01,
            MaxRouteGraphEdges = 1_000
        });

        var goodGraph = CreateTinyRouteGraphData("relevant-shard");
        var artifact = RouteGraphArtifactCodec.Pack(goodGraph);
        var payload = RouteGraphArtifactCodec.SerializeRedisPayload(artifact);
        var readResult = new RouteGraphArtifactStoreReadResult(
            artifact,
            "relevant-shard.acrg",
            payload.Length,
            DateTime.UtcNow,
            "unit-test",
            payload);
        var manifest = new RouteGraphArtifactManifest(
            RouteGraphArtifactCodec.SchemaVersion,
            RouteEdgeCostModel.Version,
            RouteEdgeCostModel.EdgeWeightVersion,
            RouteGraphPreprocessor.AltAlgorithmVersion,
            routingOptions.Value.RouteGraphShardSizeDegrees,
            "unit-test.osm",
            DateTime.UtcNow,
            new[]
            {
                new RouteGraphArtifactManifestShard(
                    "tiny-overlap-missing-shard",
                    -1.9100,
                    52.4700,
                    -1.9050,
                    52.4750,
                    1,
                    1,
                    1,
                    DateTime.UtcNow,
                    "unit-test",
                    "missing.acrg",
                    "missing"),
                new RouteGraphArtifactManifestShard(
                    goodGraph.ShardKey!,
                    -1.9100,
                    52.4700,
                    -1.8700,
                    52.5000,
                    goodGraph.Nodes.Count,
                    goodGraph.LoadedEdgeCount,
                    payload.Length,
                    DateTime.UtcNow,
                    "unit-test",
                    "relevant-shard.acrg",
                    "valid")
            });
        var artifactStore = new Mock<IRouteGraphArtifactStore>();
        artifactStore.SetupGet(store => store.IsEnabled).Returns(true);
        artifactStore
            .Setup(store => store.TryReadManifestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(manifest);
        artifactStore
            .Setup(store => store.TryReadManifestShardAsync(
                It.Is<RouteGraphArtifactManifestShard>(shard => shard.CacheKey == goodGraph.ShardKey),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(readResult);

        var repository = new RouteGraphRepository(
            dbContext,
            memoryCache,
            distributedCache,
            new AccessCityMetrics(),
            routeGraphStatus.Object,
            routingOptions,
            NullLogger<RouteGraphRepository>.Instance,
            artifactStore.Object);

        var loaded = await repository.LoadGraphAsync(
            new Coordinate(-1.8904, 52.4862),
            new Coordinate(-1.8894, 52.4862));
        var warmedShardPayload = await distributedCache.GetAsync(goodGraph.ShardKey!);

        Assert.True(loaded.HasCoverage);
        Assert.Equal(goodGraph.LoadedEdgeCount, loaded.LoadedEdgeCount);
        Assert.Equal(payload, warmedShardPayload);
        artifactStore.Verify(
            store => store.TryReadManifestShardAsync(
                It.Is<RouteGraphArtifactManifestShard>(shard => shard.CacheKey == "tiny-overlap-missing-shard"),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static string BuildRouteGraphCacheKey(
        Coordinate start,
        Coordinate end,
        RoutingOptions options,
        string graphVersion)
    {
        var edgeLimit = Math.Max(100, options.MaxRouteGraphEdges);
        var latitudeDelta = Math.Abs(start.Y - end.Y);
        var longitudeDelta = Math.Abs(start.X - end.X);
        var padding = Math.Max(0.01, Math.Max(latitudeDelta, longitudeDelta) * 0.35);
        var shardSize = Math.Clamp(options.RouteGraphShardSizeDegrees, 0.002, 0.05);
        var minLon = Math.Floor((Math.Min(start.X, end.X) - padding) / shardSize) * shardSize;
        var minLat = Math.Floor((Math.Min(start.Y, end.Y) - padding) / shardSize) * shardSize;
        var maxLon = Math.Ceiling((Math.Max(start.X, end.X) + padding) / shardSize) * shardSize;
        var maxLat = Math.Ceiling((Math.Max(start.Y, end.Y) + padding) / shardSize) * shardSize;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"route_graph:v7:{RouteGraphArtifactCodec.SchemaVersion}:ew{RouteEdgeCostModel.EdgeWeightVersion}:alt{RouteGraphPreprocessor.AltAlgorithmVersion}:region:{graphVersion}:{edgeLimit}:{minLon:F4}:{minLat:F4}:{maxLon:F4}:{maxLat:F4}");
    }

    private static RouteGraphData CreateTinyRouteGraphData(string shardKey)
    {
        var graphData = new RouteGraphData
        {
            ShardKey = shardKey,
            SourceShardKeys = new[] { shardKey },
            LoadedEdgeCount = 1,
            Nodes = new Dictionary<long, GraphNode>
            {
                [1] = new()
                {
                    Id = 1,
                    Location = new Coordinate(-1.8904, 52.4862),
                    Edges =
                    {
                        [2] = new GraphEdge { TargetNodeId = 2, DistanceMetres = 90 }
                    }
                },
                [2] = new()
                {
                    Id = 2,
                    Location = new Coordinate(-1.8894, 52.4862)
                }
            }
        };
        RouteGraphSpatialIndex.BuildSpatialBuckets(graphData);
        RouteGraphPreprocessor.TryAttachPreprocessing(graphData, new RoutingOptions
        {
            RouteGraphAltPreprocessingEnabled = true,
            RouteGraphAltLandmarkCount = 2,
            RouteGraphMaxAltPreprocessedNodes = 10
        });
        return graphData;
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

    [Fact]
    public async Task SafePathOptionsAsync_Dispatches_To_Route_Worker_And_Caches_Options()
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
                    ["Routing:AsyncFirstForCacheMiss"] = "true",
                    ["Routing:AsyncFirstCacheProbeMilliseconds"] = "0",
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

        var accepted = await client.PostAsJsonAsync("/api/v1/routing/safe-path/options", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        using var payload = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var jobId = payload.RootElement.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jobId));
        Assert.Equal(nameof(RouteJobKind.SafePathOptions), payload.RootElement.GetProperty("kind").GetString());

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
        Assert.Equal(RouteJobKind.SafePathOptions, final.Kind);
        Assert.NotNull(final.Options);
        Assert.NotNull(final.Route);
        Assert.True(final.Options!.Recommended.Distance > 0);
        Assert.Equal(final.Options.Recommended.Distance, final.Route!.Distance);

        var cached = await client.PostAsJsonAsync("/api/v1/routing/safe-path/options", request, JsonOptions);
        cached.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);

        var cachedEnvelope = await cached.Content.ReadFromJsonAsync<SafePathOptionsResponse>(JsonOptions);
        Assert.NotNull(cachedEnvelope);
        Assert.True(cachedEnvelope!.Recommended.Distance > 0);
    }

    private static GraphEdge BuildTestEdge(
        Coordinate target,
        double distanceMetres,
        bool hasStairs = false,
        bool hasBarrier = false)
    {
        var cost = RouteEdgeCostModel.Compute(
            distanceMetres,
            surface: "asphalt",
            smoothness: "good",
            hasStairs,
            hasBarrier,
            kerbHeight: 0,
            widthMetres: 1.5,
            isSteep: false,
            access: null);

        var edge = new GraphEdge
        {
            TargetNodeId = 2,
            DistanceMetres = distanceMetres,
            SurfaceType = "asphalt",
            Smoothness = "good",
            WidthMetres = 1.5,
            LightingQuality = 0.9,
            HasStairs = hasStairs,
            HasBarrier = hasBarrier,
            AccessibilityCostVersion = cost.Version,
            StandardAccessibilityPenaltySeconds = cost.StandardAccessibilityPenaltySeconds,
            WheelchairAccessibilityPenaltySeconds = cost.WheelchairAccessibilityPenaltySeconds,
            StrollerAccessibilityPenaltySeconds = cost.StrollerAccessibilityPenaltySeconds,
            AccessibilityDataQuality = cost.AccessibilityDataQuality,
            Geometry = new[] { new Coordinate(-1.8904, 52.4862), target }
        };
        RouteEdgeCostModel.PopulateTraversalWeights(edge);
        return edge;
    }
}
