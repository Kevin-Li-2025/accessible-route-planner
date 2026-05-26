using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Models.External;
using AccessCity.API.Services;
using AccessCity.API.Services.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using NetTopologySuite.Geometries;
using Xunit;

namespace AccessCity.Tests;

/// <summary>
/// Pure unit tests for <see cref="RiskScoringService"/> with mocked external dependencies.
/// No HTTP, no database — deterministic and isolated.
/// </summary>
public class RiskScoringServiceTests
{
    private static readonly GeometryFactory Wgs84 = new(new PrecisionModel(), 4326);

    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"risk_test_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static RiskScoringService CreateService(
        AppDbContext? db = null,
        IUkPoliceDataClient? ukPolice = null,
        ILiveHazardClient? weather = null,
        IMemoryCache? cache = null,
        IEnvironmentalDataClient? env = null)
    {
        db ??= CreateInMemoryDbContext();
        return new RiskScoringService(db, ukPolice, weather, cache, env);
    }

    private static HazardReport MakeHazard(double lat, double lng, string type = "pothole",
        HazardStatus status = HazardStatus.Reported)
    {
        return new HazardReport
        {
            Id = Guid.NewGuid(),
            Location = Wgs84.CreatePoint(new Coordinate(lng, lat)),
            Type = type,
            Description = "test",
            Status = status,
            ReportedAt = DateTime.UtcNow
        };
    }

    // ─────────── Haversine (pure math, deterministic) ───────────

    [Fact]
    public void HaversineDistance_SamePoint_ReturnsZero()
    {
        double d = RiskScoringService.HaversineDistance(52.48, -1.89, 52.48, -1.89);
        Assert.Equal(0.0, d, precision: 5);
    }

    [Fact]
    public void HaversineDistance_KnownPair_AccurateWithin1Percent()
    {
        // Birmingham New Street → Bullring: ~300m
        double d = RiskScoringService.HaversineDistance(52.4778, -1.8983, 52.4774, -1.8935);
        Assert.InRange(d, 300, 400);
    }

    [Fact]
    public void HaversineDistance_AntipodePair_ApproximatelyHalfEarth()
    {
        // London → approximate antipode in the Pacific
        double d = RiskScoringService.HaversineDistance(51.5, -0.12, -51.5, 179.88);
        Assert.InRange(d, 19_000_000, 21_000_000); // ~20,000 km
    }

    // ─────────── EvaluateRisk: zero hazards ───────────

    [Fact]
    public async Task EvaluateRisk_NoHazards_ReturnsMinimalRisk()
    {
        var svc = CreateService();
        var result = await svc.EvaluateRiskAsync(52.48, -1.89, 500, Enumerable.Empty<HazardReport>());

        Assert.Equal(0, result.NearbyHazardCount);
        Assert.Equal(0, result.HazardProximityRisk);
        Assert.InRange(result.OverallRisk, 0, 0.5);
    }

    [Fact]
    public async Task EvaluateRisk_HazardWithMissingLocation_IsIgnored()
    {
        var svc = CreateService();
        var hazards = new[]
        {
            new HazardReport
            {
                Id = Guid.NewGuid(),
                Location = null!,
                Type = "missing_curb_ramp",
                Status = HazardStatus.Reported,
                ReportedAt = DateTime.UtcNow
            }
        };

        var result = await svc.EvaluateRiskAsync(52.48, -1.89, 500, hazards);

        Assert.Equal(0, result.NearbyHazardCount);
        Assert.Equal(0, result.HazardProximityRisk);
    }

    // ─────────── EvaluateRisk: nearby high-severity hazard ───────────

    [Fact]
    public async Task EvaluateRisk_NearbyFloodingHazard_ElevatedProximityRisk()
    {
        var svc = CreateService();
        var hazards = new[]
        {
            MakeHazard(52.4801, -1.8901, "flooding"),  // ~10m away
        };

        var result = await svc.EvaluateRiskAsync(52.48, -1.89, 500, hazards);

        Assert.Equal(1, result.NearbyHazardCount);
        Assert.True(result.HazardProximityRisk > 0.3, $"Expected elevated proximity risk, got {result.HazardProximityRisk}");
    }

    // ─────────── EvaluateRisk: hazard outside radius ignored ───────────

    [Fact]
    public async Task EvaluateRisk_HazardOutsideRadius_NotCounted()
    {
        var svc = CreateService();
        var hazards = new[]
        {
            MakeHazard(53.0, -2.0, "pothole"),  // ~60km away
        };

        var result = await svc.EvaluateRiskAsync(52.48, -1.89, 500, hazards);

        Assert.Equal(0, result.NearbyHazardCount);
    }

    // ─────────── EvaluateRisk: resolved hazards filtered ───────────

    [Fact]
    public async Task EvaluateRisk_ResolvedHazard_IsIgnored()
    {
        var svc = CreateService();
        var hazards = new[]
        {
            MakeHazard(52.4801, -1.8901, "flooding", HazardStatus.Resolved),
        };

        var result = await svc.EvaluateRiskAsync(52.48, -1.89, 500, hazards);

        Assert.Equal(0, result.NearbyHazardCount);
    }

    // ─────────── QuickRisk: boundary conditions ───────────

    [Fact]
    public void QuickRisk_NoHazards_ReturnsLowRisk()
    {
        var svc = CreateService();
        double risk = svc.QuickRisk(52.48, -1.89, Enumerable.Empty<HazardReport>());
        Assert.Equal(0, risk);
    }

    [Fact]
    public void QuickRisk_HazardWithMissingLocation_IsIgnored()
    {
        var svc = CreateService();
        var risk = svc.QuickRisk(
            52.48,
            -1.89,
            new[]
            {
                new HazardReport
                {
                    Id = Guid.NewGuid(),
                    Location = null!,
                    Type = "missing_curb_ramp",
                    Status = HazardStatus.Reported
                }
            });

        Assert.Equal(0, risk);
    }

    [Fact]
    public void QuickRisk_ClusterOfHazards_ReturnsHighRisk()
    {
        var svc = CreateService();
        var hazards = Enumerable.Range(0, 5).Select(i =>
            MakeHazard(52.48 + i * 0.0001, -1.89, "poor_lighting")).ToList();

        double risk = svc.QuickRisk(52.48, -1.89, hazards, 500);
        Assert.True(risk > 0.4, $"Expected elevated risk for cluster, got {risk}");
    }

    [Theory]
    [InlineData("blocked-pavement", 0.75)]
    [InlineData("blocked pavement", 0.75)]
    [InlineData("missing curb ramp", 0.9)]
    [InlineData("stairs/no ramp", 0.92)]
    [InlineData("gravel", 0.65)]
    public void HazardSeverityLookup_NormalizesCommonClientAndOsmTokens(string hazardType, double expectedSeverity)
    {
        var severity = HazardSeverityLookup.GetSeverity(hazardType);

        Assert.Equal(expectedSeverity, severity, precision: 2);
    }

    [Fact]
    public void QuickRisk_UsesNormalizedAccessibilitySeverity()
    {
        var svc = CreateService();
        var defaultRisk = svc.QuickRisk(
            52.48,
            -1.89,
            new[] { MakeHazard(52.48, -1.89, "unknown_access_issue") },
            100);
        var rampRisk = svc.QuickRisk(
            52.48,
            -1.89,
            new[] { MakeHazard(52.48, -1.89, "missing curb ramp") },
            100);

        Assert.True(rampRisk > defaultRisk, $"Expected missing curb ramp risk {rampRisk} to exceed default {defaultRisk}.");
    }

    // ─────────── Crime integration with mocked IUkPoliceDataClient ───────────

    [Fact]
    public async Task EvaluateRisk_WithMockedCrimeData_IncludesCrimeRisk()
    {
        var mockPolice = new Mock<IUkPoliceDataClient>();
        var crimeList = Enumerable.Range(0, 20).Select(_ => new StreetCrimeRecord()).ToList();
        mockPolice
            .Setup(c => c.GetRecentStreetCrimesAsync(It.IsAny<double>(), It.IsAny<double>()))
            .Returns(Task.FromResult<List<StreetCrimeRecord>?>(crimeList));

        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = CreateService(ukPolice: mockPolice.Object, cache: cache);

        var result = await svc.EvaluateRiskAsync(52.48, -1.89, 500, Enumerable.Empty<HazardReport>());

        Assert.True(result.CrimeRisk > 0, "Expected non-zero crime risk with mocked crime data");
        Assert.Equal(20, result.CrimeCount);
        mockPolice.Verify(c => c.GetRecentStreetCrimesAsync(It.IsAny<double>(), It.IsAny<double>()), Times.Once);
    }

    // ─────────── Crime data caching ───────────

    [Fact]
    public async Task EvaluateRisk_CalledTwice_OnlyOneApiCall()
    {
        var mockPolice = new Mock<IUkPoliceDataClient>();
        mockPolice
            .Setup(c => c.GetRecentStreetCrimesAsync(It.IsAny<double>(), It.IsAny<double>()))
            .Returns(Task.FromResult<List<StreetCrimeRecord>?>(new List<StreetCrimeRecord>()));

        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = CreateService(ukPolice: mockPolice.Object, cache: cache);

        await svc.EvaluateRiskAsync(52.48, -1.89, 500, Enumerable.Empty<HazardReport>());
        await svc.EvaluateRiskAsync(52.48, -1.89, 500, Enumerable.Empty<HazardReport>());

        // Second call should hit cache — only one API call total
        mockPolice.Verify(c => c.GetRecentStreetCrimesAsync(It.IsAny<double>(), It.IsAny<double>()), Times.Once);
    }

    // ─────────── QuickCrimeRisk: cache miss returns baseline ───────────

    [Fact]
    public void QuickCrimeRisk_NoCachedData_ReturnsBaseline()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = CreateService(cache: cache);
        double risk = svc.QuickCrimeRisk(52.48, -1.89);
        Assert.Equal(0.15, risk);
    }

    [Fact]
    public void QuickInfrastructureRisk_NoCachedData_ReturnsBaseline()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = CreateService(cache: cache);

        double risk = svc.QuickInfrastructureRisk(52.48, -1.89);

        Assert.Equal(0.35, risk);
    }

    [Fact]
    public void QuickInfrastructureRisk_UsesWarmMemoryCache()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("infra-risk:52.4800:-1.8900:150", 0.72);
        var svc = CreateService(cache: cache);

        double risk = svc.QuickInfrastructureRisk(52.48, -1.89);

        Assert.Equal(0.72, risk);
    }

    [Fact]
    public void QuickInfrastructureRisk_UsesRequestedRadiusBucket()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("infra-risk:52.4800:-1.8900:200", 0.81);
        var svc = CreateService(cache: cache);

        double risk = svc.QuickInfrastructureRisk(52.48, -1.89, 200);

        Assert.Equal(0.81, risk);
    }

    [Fact]
    public async Task EstimateInfrastructureRiskAsync_CoalescesConcurrentColdRequests()
    {
        await using var db = CreateInMemoryDbContext();
        db.RouteNodes.AddRange(
            new RouteNode
            {
                Id = 1,
                Location = Wgs84.CreatePoint(new Coordinate(-1.8904, 52.48))
            },
            new RouteNode
            {
                Id = 2,
                Location = Wgs84.CreatePoint(new Coordinate(-1.8900, 52.4804))
            });
        db.RouteEdges.Add(new RouteEdge
        {
            Id = 1,
            FromNodeId = 1,
            ToNodeId = 2,
            Geometry = Wgs84.CreateLineString(new[]
            {
                new Coordinate(-1.8904, 52.48),
                new Coordinate(-1.8900, 52.4804)
            }),
            LightingQuality = 0.2,
            HasStairs = true,
            KerbHeight = 0.08,
            DistanceMetres = 50
        });
        await db.SaveChangesAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = CreateService(db: db, cache: cache);

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => svc.EstimateInfrastructureRiskAsync(52.48, -1.89, 200))
            .ToArray();
        var risks = await Task.WhenAll(tasks);

        Assert.All(risks, risk => Assert.Equal(risks[0], risk));
        Assert.Equal(risks[0], svc.QuickInfrastructureRisk(52.48, -1.89, 200));
    }

    // ─────────── Environmental data mocking ───────────

    [Fact]
    public async Task EvaluateRisk_WithMockedLighting_ReflectsLightingRisk()
    {
        var mockEnv = new Mock<IEnvironmentalDataClient>();
        mockEnv
            .Setup(e => e.GetNearbyInfrastructureAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>()))
            .ReturnsAsync(new EnvironmentalSummary { StreetLampCount = 0, SurveillanceCameraCount = 0 });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = CreateService(cache: cache, env: mockEnv.Object);

        var result = await svc.EvaluateRiskAsync(52.48, -1.89, 500, Enumerable.Empty<HazardReport>());

        // Zero street lamps = maximum lighting risk (1.0)
        Assert.True(result.LightingRisk >= 0.8, $"Expected high lighting risk with 0 lamps, got {result.LightingRisk}");
    }

    [Fact]
    public async Task EvaluateRisk_WellLitArea_LowLightingRisk()
    {
        var mockEnv = new Mock<IEnvironmentalDataClient>();
        mockEnv
            .Setup(e => e.GetNearbyInfrastructureAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>()))
            .ReturnsAsync(new EnvironmentalSummary { StreetLampCount = 15, SurveillanceCameraCount = 3 });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = CreateService(cache: cache, env: mockEnv.Object);

        var result = await svc.EvaluateRiskAsync(52.48, -1.89, 500, Enumerable.Empty<HazardReport>());

        Assert.True(result.LightingRisk <= 0.2, $"Expected low lighting risk with 15 lamps, got {result.LightingRisk}");
        Assert.True(result.SurveillanceRisk <= 0.2, $"Expected low surveillance risk with 3 cameras, got {result.SurveillanceRisk}");
    }

    // ─────────── Hazard severity weighting ───────────

    [Fact]
    public async Task EvaluateRisk_FloodingVsPothole_FloodingWeighsMore()
    {
        var svc = CreateService();

        var floodResult = await svc.EvaluateRiskAsync(52.48, -1.89, 500, new[]
        {
            MakeHazard(52.4801, -1.8901, "flooding") // severity 0.9
        });

        var potholeResult = await svc.EvaluateRiskAsync(52.48, -1.89, 500, new[]
        {
            MakeHazard(52.4801, -1.8901, "pothole") // severity 0.6
        });

        Assert.True(floodResult.HazardProximityRisk > potholeResult.HazardProximityRisk,
            $"Flooding ({floodResult.HazardProximityRisk}) should weigh more than pothole ({potholeResult.HazardProximityRisk})");
    }
}
