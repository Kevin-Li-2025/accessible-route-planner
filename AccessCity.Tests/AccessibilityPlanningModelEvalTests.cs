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
            BuildEdge(3, 52.482, -1.902, "gravel", "bad", 0.8, hasStairs: false, hasBarrier: false, kerbHeight: 0.02),
            BuildEdge(4, 52.483, -1.903, "unknown", "good", 1.8, hasStairs: false, hasBarrier: false, kerbHeight: 0),
            BuildEdge(5, 52.484, -1.904, "asphalt", "good", 1.8, hasStairs: false, hasBarrier: false, kerbHeight: 0),
            BuildEdge(6, 52.485, -1.905, "asphalt", null, 1.5, hasStairs: false, hasBarrier: false, kerbHeight: 0.05),
            BuildEdge(7, 52.486, -1.906, "unknown", null, null, hasStairs: true, hasBarrier: false, kerbHeight: 0),
            BuildEdge(8, 52.487, -1.907, "paving_stones", "intermediate", null, hasStairs: false, hasBarrier: false, kerbHeight: 0));
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
        var labels = new Dictionary<long, int>
        {
            [1] = 3,
            [2] = 2,
            [3] = 2,
            [4] = 1,
            [5] = 0,
            [6] = 2,
            [7] = 3,
            [8] = 1
        };
        var evalRows = summary.RepairCandidates
            .Select((candidate, index) => new AccessibilityRankerEvalRow(
                index + 1,
                candidate.EdgeId,
                labels.GetValueOrDefault(candidate.EdgeId),
                candidate.ModelScore,
                candidate.ModelConfidence,
                candidate.ActiveLearningScore,
                candidate.PriorityScore,
                candidate.ReviewStrategy,
                candidate.FeatureContributions
                    .OrderByDescending(contribution => Math.Abs(contribution.Contribution))
                    .Take(3)
                    .Select(contribution => contribution.Feature)
                    .ToArray()))
            .ToArray();
        var metrics = BuildMetrics(evalRows, labels, relevantThreshold: 2);
        var eval = new
        {
            HarnessVersion = "accesscity-accessibility-ranker-eval-v2",
            GeneratedAtUtc = DateTime.UtcNow,
            summary.RankingModelVersion,
            summary.RankingModelKind,
            Dataset = new
            {
                Name = "accessibility-repair-ranker-fixture-v1",
                Area = "Synthetic Birmingham-style route-edge metadata fixture",
                EdgeCount = labels.Count,
                RelevanceScale = "0=no review needed, 1=low information value, 2=high review value, 3=critical accessibility blocker",
                ClaimBoundary = "Deterministic regression fixture; not a substitute for a held-out city-scale labeled dataset."
            },
            CandidateCount = summary.RepairCandidates.Count,
            TopEdgeId = top.EdgeId,
            TopModelScore = top.ModelScore,
            TopModelConfidence = top.ModelConfidence,
            TopActiveLearningScore = top.ActiveLearningScore,
            TopReviewStrategy = top.ReviewStrategy,
            TopFeatureContributions = top.FeatureContributions,
            Metrics = metrics,
            Rows = evalRows,
            StrategyCounts = evalRows
                .GroupBy(row => row.ReviewStrategy, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            RankedEdgeIds = rankedIds,
            Passed = labels[top.EdgeId] == 3
                     && top.ModelScore > 0.8
                     && top.ActiveLearningScore > 50
                     && metrics.NdcgAt5 >= 0.9
                     && metrics.PrecisionAt3 >= 0.66
                     && metrics.RecallAt3 >= 0.5
                     && top.FeatureContributions.Count >= 3
        };

        var reportDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TestResults/accesscity-ai-model-eval"));
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, "accessibility_ranker_eval_report.json");
        var summaryPath = Path.Combine(reportDir, "accessibility_ranker_eval_summary.md");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(eval, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(summaryPath, BuildMarkdownSummary(
            eval.GeneratedAtUtc,
            summary.RankingModelVersion,
            summary.RankingModelKind,
            metrics,
            evalRows));
        _output.WriteLine($"Accessibility ranker eval report: {reportPath}");
        _output.WriteLine($"Accessibility ranker eval summary: {summaryPath}");

        Assert.True(eval.Passed);
        Assert.Equal(3, labels[top.EdgeId]);
        Assert.True(top.ModelScore > 0.8);
        Assert.True(top.ActiveLearningScore > 50);
        Assert.True(metrics.NdcgAt5 >= 0.9);
        Assert.True(metrics.PrecisionAt3 >= 0.66);
        Assert.True(metrics.RecallAt3 >= 0.5);
        Assert.Contains(top.FeatureContributions, contribution => contribution.Feature == "blockerScore");
    }

    private static AccessibilityRankerMetrics BuildMetrics(
        IReadOnlyList<AccessibilityRankerEvalRow> rows,
        IReadOnlyDictionary<long, int> labels,
        int relevantThreshold)
    {
        var precisionAt3 = rows.Take(3).Count(row => row.Relevance >= relevantThreshold) / 3.0;
        var totalRelevant = labels.Values.Count(label => label >= relevantThreshold);
        var recallAt3 = totalRelevant == 0
            ? 1
            : rows.Take(3).Count(row => row.Relevance >= relevantThreshold) / (double)totalRelevant;
        var ndcgAt5 = DiscountedCumulativeGain(rows.Take(5).Select(row => row.Relevance))
                      / Math.Max(1e-9, DiscountedCumulativeGain(labels.Values.OrderDescending().Take(5)));
        var meanTop3ActiveLearningScore = rows.Take(3).Average(row => row.ActiveLearningScore);

        return new AccessibilityRankerMetrics(
            Math.Round(ndcgAt5, 4),
            Math.Round(precisionAt3, 4),
            Math.Round(recallAt3, 4),
            Math.Round(meanTop3ActiveLearningScore, 2),
            rows.FirstOrDefault()?.Relevance ?? 0);
    }

    private static double DiscountedCumulativeGain(IEnumerable<int> relevanceScores)
    {
        return relevanceScores
            .Select((relevance, index) => (Math.Pow(2, relevance) - 1) / Math.Log2(index + 2))
            .Sum();
    }

    private static string BuildMarkdownSummary(
        DateTime generatedAtUtc,
        string modelVersion,
        string modelKind,
        AccessibilityRankerMetrics metrics,
        IReadOnlyList<AccessibilityRankerEvalRow> rows)
    {
        var lines = new List<string>
        {
            "# Accessibility Repair Ranker Evaluation",
            string.Empty,
            $"Generated at: `{generatedAtUtc:O}`",
            $"Model: `{modelVersion}`",
            $"Kind: `{modelKind}`",
            string.Empty,
            "## Metrics",
            string.Empty,
            "| Metric | Value | Gate |",
            "| --- | ---: | ---: |",
            $"| NDCG@5 | {metrics.NdcgAt5:F4} | >= 0.9000 |",
            $"| Precision@3 | {metrics.PrecisionAt3:F4} | >= 0.6600 |",
            $"| Recall@3 | {metrics.RecallAt3:F4} | >= 0.5000 |",
            $"| Mean top-3 active-learning score | {metrics.MeanTop3ActiveLearningScore:F2} | > 0 |",
            $"| Top relevance | {metrics.TopRelevance} | 3 |",
            string.Empty,
            "## Ranked Fixture Rows",
            string.Empty,
            "| Rank | Edge | Relevance | Model score | Active learning | Priority | Strategy | Top features |",
            "| ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |"
        };

        lines.AddRange(rows.Select(row =>
            $"| {row.Rank} | {row.EdgeId} | {row.Relevance} | {row.ModelScore:F4} | {row.ActiveLearningScore:F2} | {row.PriorityScore:F2} | `{row.ReviewStrategy}` | {string.Join(", ", row.TopFeatures)} |"));
        lines.AddRange(
        [
            string.Empty,
            "## Claim Boundary",
            string.Empty,
            "This is a deterministic regression fixture for the local auditable ranker. It proves ordering, explanations, active-learning signals, and guardrails are reproducible; it does not prove city-wide model generalization without a held-out labeled dataset."
        ]);

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
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

    private sealed record AccessibilityRankerEvalRow(
        int Rank,
        long EdgeId,
        int Relevance,
        double ModelScore,
        double ModelConfidence,
        double ActiveLearningScore,
        double PriorityScore,
        string ReviewStrategy,
        IReadOnlyList<string> TopFeatures);

    private sealed record AccessibilityRankerMetrics(
        double NdcgAt5,
        double PrecisionAt3,
        double RecallAt3,
        double MeanTop3ActiveLearningScore,
        int TopRelevance);
}
