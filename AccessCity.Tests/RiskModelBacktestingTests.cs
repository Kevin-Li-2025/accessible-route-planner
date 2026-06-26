using System.Text.Json;
using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;
using Xunit.Abstractions;

namespace AccessCity.Tests;

public class RiskModelBacktestingTests
{
    private readonly ITestOutputHelper _output;
    private static readonly GeometryFactory Wgs84 = new(new PrecisionModel(), 4326);

    public RiskModelBacktestingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task PredictiveRiskModel_BacktestReport_EnforcesDeterministicRiskOrdering()
    {
        await using var db = CreateInMemoryDbContext();
        var baseRisk = new RiskScoringService(db);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RiskScoring:RealtimeExternalSignalsEnabled"] = "false"
            })
            .Build();
        var model = new PredictiveRiskModel(
            baseRisk,
            new HazardRiskGrid(),
            cache: cache,
            configuration: configuration);

        var cases = new[]
        {
            new BacktestCase("no_hazards", Array.Empty<HazardReport>(), 0),
            new BacktestCase("far_pothole", [MakeHazard(52.4890, -1.8990, "pothole")], 1),
            new BacktestCase("near_pothole", [MakeHazard(52.4804, -1.8904, "pothole")], 2),
            new BacktestCase("near_flooding", [MakeHazard(52.4804, -1.8904, "flooding")], 3),
            new BacktestCase("clustered_accessibility_hazards",
            [
                MakeHazard(52.4804, -1.8904, "missing_curb_ramp"),
                MakeHazard(52.4805, -1.8905, "blocked_pavement"),
                MakeHazard(52.4806, -1.8906, "construction")
            ], 4)
        };

        var results = new List<BacktestResult>();
        foreach (var testCase in cases)
        {
            var risk = await model.EvaluateSegmentRiskAsync(52.4800, -1.8900, testCase.Hazards, radiusMetres: 250);
            results.Add(new BacktestResult(
                testCase.Name,
                testCase.ExpectedRiskRank,
                risk.OverallRisk,
                risk.HazardRisk,
                risk.InfrastructureRisk,
                risk.RiskFactors));
        }

        var orderedByExpected = results.OrderBy(result => result.ExpectedRiskRank).ToArray();
        for (var i = 1; i < orderedByExpected.Length; i++)
        {
            Assert.True(
                orderedByExpected[i].OverallRisk >= orderedByExpected[i - 1].OverallRisk,
                $"{orderedByExpected[i].Name} should not score below {orderedByExpected[i - 1].Name}");
        }

        Assert.True(results.Single(result => result.Name == "near_flooding").HazardRisk
                    > results.Single(result => result.Name == "near_pothole").HazardRisk);
        Assert.True(results.Single(result => result.Name == "clustered_accessibility_hazards").OverallRisk
                    > results.Single(result => result.Name == "near_pothole").OverallRisk);

        var report = new RiskModelBacktestReport(
            "accesscity-risk-model-backtest-v1",
            "deterministic-fixed-weight-risk-model",
            DateTime.UtcNow,
            new RiskModelBacktestSummary(
                results.Count,
                "passed",
                Math.Round(results.Min(result => result.OverallRisk), 4),
                Math.Round(results.Max(result => result.OverallRisk), 4)),
            results);

        var reportDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TestResults/accesscity-risk-backtest"));
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, "risk_model_backtest_report.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        _output.WriteLine($"Risk model backtest report: {reportPath}");
    }

    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"risk_model_backtest_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static HazardReport MakeHazard(double lat, double lng, string type) => new()
    {
        Id = Guid.NewGuid(),
        Location = Wgs84.CreatePoint(new Coordinate(lng, lat)),
        Type = type,
        Description = type,
        Status = HazardStatus.Reported,
        ReportedAt = DateTime.UtcNow
    };

    private sealed record BacktestCase(
        string Name,
        IReadOnlyList<HazardReport> Hazards,
        int ExpectedRiskRank);

    private sealed record BacktestResult(
        string Name,
        int ExpectedRiskRank,
        double OverallRisk,
        double HazardRisk,
        double InfrastructureRisk,
        IReadOnlyList<string> RiskFactors);

    private sealed record RiskModelBacktestSummary(
        int CaseCount,
        string Status,
        double MinOverallRisk,
        double MaxOverallRisk);

    private sealed record RiskModelBacktestReport(
        string HarnessVersion,
        string ModelKind,
        DateTime GeneratedAtUtc,
        RiskModelBacktestSummary Summary,
        IReadOnlyList<BacktestResult> Results);
}
