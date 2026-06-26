using System.Text.Json;
using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Xunit.Abstractions;

namespace AccessCity.Tests;

public sealed class AccessibilityPlanningModelEvalTests
{
    private readonly ITestOutputHelper _output;

    public AccessibilityPlanningModelEvalTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task RepairRanker_EmitsOfflineEvalReport_AndOrdersRiskyEdgesFirst()
    {
        await using var db = CreateDbContext();
        db.RouteEdges.AddRange(
            BuildEdge(1, 52.480, -1.900, "unknown", null, null, hasStairs: false, hasBarrier: true, kerbHeight: 0.08),
            BuildEdge(2, 52.481, -1.901, "paving_stones", null, null, hasStairs: false, hasBarrier: false, kerbHeight: 0.04),
            BuildEdge(3, 52.482, -1.902, "asphalt", "good", 1.8, hasStairs: false, hasBarrier: false, kerbHeight: 0),
            BuildEdge(4, 52.483, -1.903, "asphalt", "good", 2.2, hasStairs: false, hasBarrier: false, kerbHeight: 0));
        await db.SaveChangesAsync();

        var service = new AccessibilityPlanningService(db);
        var summary = await service.AnalyzeAsync(
            new AccessibilityPlanningRequest
            {
                MinLatitude = 52.47,
                MinLongitude = -1.91,
                MaxLatitude = 52.49,
                MaxLongitude = -1.89,
                Profile = "manual-wheelchair",
                MaxCandidates = 10
            },
            CancellationToken.None);

        var rankedIds = summary.RepairCandidates.Select(candidate => candidate.EdgeId).ToList();
        var top = summary.RepairCandidates.First();
        var eval = new
        {
            HarnessVersion = "accesscity-accessibility-ranker-eval-v1",
            GeneratedAtUtc = DateTime.UtcNow,
            summary.RankingModelVersion,
            summary.RankingModelKind,
            CandidateCount = summary.RepairCandidates.Count,
            TopEdgeId = top.EdgeId,
            TopModelScore = top.ModelScore,
            TopModelConfidence = top.ModelConfidence,
            TopFeatureContributions = top.FeatureContributions,
            RankedEdgeIds = rankedIds,
            Passed = top.EdgeId == 1 && top.ModelScore > 0.8 && top.FeatureContributions.Count >= 3
        };

        var reportDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TestResults/accesscity-ai-model-eval"));
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, "accessibility_ranker_eval_report.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(eval, new JsonSerializerOptions { WriteIndented = true }));
        _output.WriteLine($"Accessibility ranker eval report: {reportPath}");

        Assert.True(eval.Passed);
        Assert.Equal(1, top.EdgeId);
        Assert.True(top.ModelScore > 0.8);
        Assert.Contains(top.FeatureContributions, contribution => contribution.Feature == "blockerScore");
    }

    private static RoutingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<RoutingDbContext>()
            .UseInMemoryDatabase($"accessibility_model_eval_{Guid.NewGuid():N}")
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
        double kerbHeight)
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
            DistanceMetres = 100 + id * 20,
            SurfaceType = surface,
            Smoothness = smoothness,
            WidthMetres = widthMetres,
            HasStairs = hasStairs,
            HasBarrier = hasBarrier,
            KerbHeight = kerbHeight,
            AccessibilityDataQuality = RouteEdgeCostModel.ComputeAccessibilityDataQuality(surface, smoothness, widthMetres)
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
