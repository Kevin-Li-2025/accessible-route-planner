using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Models;
using AccessCity.API.Serialization;
using Xunit.Abstractions;

namespace AccessCity.Tests;

[Collection("Integration")]
public class RouteEvaluationHarnessTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;
    private readonly ITestOutputHelper _output;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new CoordinateJsonConverter(), new NetTopologySuite.IO.Converters.GeoJsonConverterFactory() }
    };

    public RouteEvaluationHarnessTests(AccessCityApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task GoldenRoutes_EmitEvalReport_AndMeetQualityGates()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden-routes.json");
        var routes = JsonSerializer.Deserialize<GoldenRouteCase[]>(
            await File.ReadAllTextAsync(fixturePath),
            JsonOptions) ?? [];
        Assert.True(routes.Length >= 3, "Golden route fixture should cover the core routing profiles.");

        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        var results = new List<GoldenRouteEvalResult>();
        foreach (var route in routes)
        {
            var request = new
            {
                Start = new { Lat = route.StartLat, Lng = route.StartLng },
                End = new { Lat = route.EndLat, Lng = route.EndLng },
                Profile = route.Profile,
                SafetyWeight = route.SafetyWeight,
                Preferences = route.Preferences
            };

            var stopwatch = Stopwatch.StartNew();
            var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path/options", request, JsonOptions);
            stopwatch.Stop();

            SafePathOptionsResponse? payload = null;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                payload = await response.Content.ReadFromJsonAsync<SafePathOptionsResponse>(JsonOptions);
            }

            var recommended = payload?.Recommended;
            var success = response.StatusCode == HttpStatusCode.OK
                          && payload is not null
                          && recommended?.Path is not null
                          && (!route.ExpectRoute || recommended.Distance > 0)
                          && recommended.Distance <= route.MaxDistanceMetres
                          && recommended.SafetyScore >= route.MinSafetyScore
                          && stopwatch.ElapsedMilliseconds <= route.MaxLatencyMs;

            results.Add(new GoldenRouteEvalResult(
                route.Name,
                response.StatusCode.ToString(),
                success,
                stopwatch.ElapsedMilliseconds,
                recommended?.Distance ?? 0,
                recommended?.EstimatedTime ?? 0,
                recommended?.SafetyScore ?? 0,
                payload?.Diagnostics.CandidateCount ?? 0,
                payload?.Diagnostics.ParetoEfficientCount ?? 0,
                payload?.Diagnostics.RecommendedPerformance));
        }

        var report = GoldenRouteEvalReport.From(results);
        var reportDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TestResults/accesscity-route-eval"));
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, "eval_report.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        _output.WriteLine($"Route eval report: {reportPath}");
        _output.WriteLine(JsonSerializer.Serialize(report.Summary, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(routes.Length, report.Summary.TotalRoutes);
        Assert.Equal(routes.Length, report.Summary.SucceededRoutes);
        Assert.True(report.Summary.SuccessRate >= 1.0);
        Assert.True(report.Summary.P95LatencyMs <= routes.Max(route => route.MaxLatencyMs));
        Assert.True(report.Summary.P99LatencyMs <= routes.Max(route => route.MaxLatencyMs));
        Assert.All(report.Results, result =>
        {
            Assert.True(result.CandidateCount >= 1, $"{result.Name} should expose at least one candidate route.");
            Assert.True(result.ParetoEfficientCount >= 1, $"{result.Name} should expose a Pareto frontier.");
        });
    }

    private sealed record GoldenRouteCase(
        string Name,
        string Profile,
        double SafetyWeight,
        string[] Preferences,
        double StartLat,
        double StartLng,
        double EndLat,
        double EndLng,
        bool ExpectRoute,
        int MaxLatencyMs,
        double MaxDistanceMetres,
        double MinSafetyScore);

    private sealed record GoldenRouteEvalResult(
        string Name,
        string StatusCode,
        bool Succeeded,
        long LatencyMs,
        double DistanceMetres,
        double EstimatedTimeMinutes,
        double SafetyScore,
        int CandidateCount,
        int ParetoEfficientCount,
        RoutePerformanceDiagnostics? Performance);

    private sealed record GoldenRouteEvalSummary(
        int TotalRoutes,
        int SucceededRoutes,
        double SuccessRate,
        double P50LatencyMs,
        double P95LatencyMs,
        double P99LatencyMs,
        double MeanDistanceMetres,
        double MeanSafetyScore,
        int TotalNodesExpanded,
        int TotalEdgesRelaxed,
        int TotalRiskLookups,
        double RiskCacheHitRate);

    private sealed record GoldenRouteEvalReport(
        string HarnessVersion,
        DateTime GeneratedAtUtc,
        GoldenRouteEvalSummary Summary,
        IReadOnlyList<GoldenRouteEvalResult> Results)
    {
        public static GoldenRouteEvalReport From(IReadOnlyList<GoldenRouteEvalResult> results)
        {
            var latencies = results.Select(result => (double)result.LatencyMs).Order().ToArray();
            var succeeded = results.Count(result => result.Succeeded);
            var riskLookups = results.Sum(result => result.Performance?.RiskLookups ?? 0);
            var riskHits = results.Sum(result => result.Performance?.RiskCacheHits ?? 0);
            var summary = new GoldenRouteEvalSummary(
                results.Count,
                succeeded,
                results.Count == 0 ? 0 : Math.Round((double)succeeded / results.Count, 4),
                Percentile(latencies, 0.50),
                Percentile(latencies, 0.95),
                Percentile(latencies, 0.99),
                Math.Round(results.Average(result => result.DistanceMetres), 2),
                Math.Round(results.Average(result => result.SafetyScore), 4),
                results.Sum(result => result.Performance?.NodesExpanded ?? 0),
                results.Sum(result => result.Performance?.EdgesRelaxed ?? 0),
                riskLookups,
                riskLookups == 0 ? 0 : Math.Round((double)riskHits / riskLookups, 4));

            return new GoldenRouteEvalReport(
                "accesscity-route-eval-v1",
                DateTime.UtcNow,
                summary,
                results);
        }

        private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
        {
            if (sortedValues.Count == 0)
            {
                return 0;
            }

            var index = Math.Clamp((int)Math.Ceiling(sortedValues.Count * percentile) - 1, 0, sortedValues.Count - 1);
            return Math.Round(sortedValues[index], 2);
        }
    }
}
