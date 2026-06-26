using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace AccessCity.Tests;

public sealed class AccessibilityPlanningServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_summarizes_quality_and_ranks_repair_candidates()
    {
        await using var db = CreateDbContext();
        db.RouteEdges.AddRange(
            BuildEdge(
                id: 1,
                lat: 52.48,
                lon: -1.90,
                surface: "unknown",
                smoothness: null,
                widthMetres: null,
                hasStairs: false,
                hasBarrier: true,
                kerbHeight: 0.08,
                accessibilityDataQuality: 0.1),
            BuildEdge(
                id: 2,
                lat: 52.481,
                lon: -1.901,
                surface: "asphalt",
                smoothness: "good",
                widthMetres: 1.8,
                hasStairs: false,
                hasBarrier: false,
                kerbHeight: 0.0,
                accessibilityDataQuality: 1.0),
            BuildEdge(
                id: 3,
                lat: 53.00,
                lon: -2.50,
                surface: "unknown",
                smoothness: null,
                widthMetres: null,
                hasStairs: true,
                hasBarrier: false,
                kerbHeight: 0.0,
                accessibilityDataQuality: 0.1));
        await db.SaveChangesAsync();

        var service = new AccessibilityPlanningService(db);

        var result = await service.AnalyzeAsync(
            new AccessibilityPlanningRequest
            {
                MinLatitude = 52.47,
                MinLongitude = -1.91,
                MaxLatitude = 52.49,
                MaxLongitude = -1.89,
                Profile = "manual-wheelchair",
                MaxCandidates = 5
            },
            CancellationToken.None);

        Assert.Equal(2, result.EdgeCount);
        Assert.Equal("manual-wheelchair", result.Profile);
        Assert.Equal("accessibility-repair-ranker-v1", result.RankingModelVersion);
        Assert.Equal("auditable-logistic-linear-ranker", result.RankingModelKind);
        Assert.Equal(0.5, result.MissingSurfaceShare);
        Assert.Equal(0.5, result.MissingSmoothnessShare);
        Assert.Equal(0.5, result.MissingWidthShare);
        Assert.Equal(0.5, result.BarrierOrStairsShare);
        Assert.Single(result.RepairCandidates);
        Assert.Equal(1, result.RepairCandidates[0].EdgeId);
        Assert.True(result.RepairCandidates[0].PriorityScore > 50);
        Assert.True(result.RepairCandidates[0].EstimatedPenaltyReductionSeconds > 0);
        Assert.True(result.RepairCandidates[0].PenaltyReductionPer100Metres > 0);
        Assert.True(result.RepairCandidates[0].DataUncertaintyPenalty > 0);
        Assert.True(result.RepairCandidates[0].AccessibilityAlpha > 0);
        Assert.Equal("accessibility-repair-ranker-v1", result.RepairCandidates[0].ModelVersion);
        Assert.InRange(result.RepairCandidates[0].ModelScore, 0, 1);
        Assert.True(result.RepairCandidates[0].ModelConfidence >= 0.6);
        Assert.Contains(result.RepairCandidates[0].FeatureContributions, contribution => contribution.Feature == "dataGap");
        Assert.Equal("critical", result.RepairCandidates[0].ReviewPriority);
        Assert.Single(result.EfficientFrontier);
        Assert.Equal(1, result.EfficientFrontier[0].EdgeId);
        Assert.Contains(result.RepairCandidates[0].Reasons, reason => reason.Contains("missing surface", StringComparison.Ordinal));
        Assert.Contains(result.Guardrails, guardrail => guardrail.Contains("review-only", StringComparison.Ordinal));
        Assert.Contains(result.Guardrails, guardrail => guardrail.Contains("auditable linear model", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_rejects_invalid_bounds()
    {
        await using var db = CreateDbContext();
        var service = new AccessibilityPlanningService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyzeAsync(
            new AccessibilityPlanningRequest
            {
                MinLatitude = 95,
                MinLongitude = -1.91,
                MaxLatitude = 96,
                MaxLongitude = -1.89
            },
            CancellationToken.None));
    }

    private static RoutingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<RoutingDbContext>()
            .UseInMemoryDatabase($"accessibility_planning_{Guid.NewGuid():N}")
            .Options;

        return new RoutingDbContext(options);
    }

    private static RouteEdge BuildEdge(
        long id,
        double lat,
        double lon,
        string surface,
        string? smoothness,
        double? widthMetres,
        bool hasStairs,
        bool hasBarrier,
        double kerbHeight,
        double accessibilityDataQuality)
    {
        var edge = new RouteEdge
        {
            Id = id,
            FromNodeId = id * 10,
            ToNodeId = id * 10 + 1,
            Geometry = new LineString(
            [
                new Coordinate(lon, lat),
                new Coordinate(lon + 0.001, lat + 0.001)
            ])
            { SRID = 4326 },
            DistanceMetres = 120,
            SurfaceType = surface,
            Smoothness = smoothness,
            WidthMetres = widthMetres,
            HasStairs = hasStairs,
            HasBarrier = hasBarrier,
            KerbHeight = kerbHeight,
            AccessibilityDataQuality = accessibilityDataQuality
        };

        var cost = RouteEdgeCostModel.Compute(
            edge.DistanceMetres,
            edge.SurfaceType,
            edge.Smoothness,
            edge.HasStairs,
            edge.HasBarrier,
            edge.KerbHeight,
            edge.WidthMetres,
            edge.IsSteep,
            edge.Access);
        edge.AccessibilityCostVersion = cost.Version;
        edge.StandardAccessibilityPenaltySeconds = cost.StandardAccessibilityPenaltySeconds;
        edge.WheelchairAccessibilityPenaltySeconds = cost.WheelchairAccessibilityPenaltySeconds;
        edge.StrollerAccessibilityPenaltySeconds = cost.StrollerAccessibilityPenaltySeconds;

        return edge;
    }
}
