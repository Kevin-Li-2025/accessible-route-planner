using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using System.Text.Json;

namespace AccessCity.API.Services;

public interface IAccessibilityPlanningService
{
    Task<AccessibilityDataQualitySummary> AnalyzeAsync(
        AccessibilityPlanningRequest request,
        CancellationToken cancellationToken);
}

public sealed class AccessibilityPlanningService : IAccessibilityPlanningService
{
    private const int MaxEdgesToAnalyze = 25_000;
    private const string RankingModelVersion = "accessibility-repair-ranker-v1";
    private const string RankingModelKind = "auditable-logistic-linear-ranker";
    private static readonly Lazy<AccessibilityRepairRankerModel> RankerModel = new(LoadRankerModel);
    private readonly RoutingDbContext _db;

    public AccessibilityPlanningService(RoutingDbContext db)
    {
        _db = db;
    }

    public async Task<AccessibilityDataQualitySummary> AnalyzeAsync(
        AccessibilityPlanningRequest request,
        CancellationToken cancellationToken)
    {
        var bounds = NormalizeBounds(request);
        var profile = NormalizeProfile(request.Profile);
        var maxCandidates = Math.Clamp(request.MaxCandidates, 1, 100);
        var searchArea = BuildSearchArea(bounds);

        var edges = await _db.RouteEdges
            .AsNoTracking()
            .Where(edge => edge.Geometry != null && edge.Geometry.Intersects(searchArea))
            .OrderBy(edge => edge.Id)
            .Take(MaxEdgesToAnalyze)
            .ToListAsync(cancellationToken);

        var scopedEdges = edges
            .Where(edge => IsInsideBounds(edge, bounds))
            .ToList();

        var totalDistance = scopedEdges.Sum(edge => Math.Max(0, edge.DistanceMetres));
        var weightedQuality = totalDistance <= 0
            ? Average(scopedEdges, edge => ResolveDataQuality(edge))
            : scopedEdges.Sum(edge => ResolveDataQuality(edge) * Math.Max(0, edge.DistanceMetres)) / totalDistance;

        var edgeFeatures = scopedEdges
            .Select(edge => BuildFeatures(edge, profile))
            .ToList();

        var candidates = edgeFeatures
            .Select(BuildCandidate)
            .Where(candidate => candidate.PriorityScore > 0)
            .OrderByDescending(candidate => candidate.PriorityScore)
            .ThenByDescending(candidate => candidate.EstimatedPenaltyReductionSeconds)
            .ThenBy(candidate => candidate.EdgeId)
            .Take(maxCandidates)
            .ToList();

        return new AccessibilityDataQualitySummary
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Profile = profile,
            RankingModelVersion = RankingModelVersion,
            RankingModelKind = RankingModelKind,
            EdgeCount = scopedEdges.Count,
            TotalDistanceMetres = Math.Round(totalDistance, 2),
            AverageDataQuality = Math.Round(Average(scopedEdges, edge => ResolveDataQuality(edge)), 4),
            DistanceWeightedDataQuality = Math.Round(weightedQuality, 4),
            MissingSurfaceShare = Math.Round(Share(scopedEdges, HasMissingSurface), 4),
            MissingSmoothnessShare = Math.Round(Share(scopedEdges, edge => string.IsNullOrWhiteSpace(edge.Smoothness)), 4),
            MissingWidthShare = Math.Round(Share(scopedEdges, edge => !edge.WidthMetres.HasValue), 4),
            BarrierOrStairsShare = Math.Round(Share(scopedEdges, edge => edge.HasBarrier || edge.HasStairs), 4),
            HighPenaltyShare = Math.Round(Share(scopedEdges, edge => ResolvePenalty(edge, profile) >= 300), 4),
            RepairCandidates = candidates,
            EfficientFrontier = BuildEfficientFrontier(candidates),
            Guardrails =
            [
                "Planning analysis is deterministic and review-only.",
                "Ranking uses a local, versioned, auditable linear model with feature contributions.",
                "Counterfactual repair scores do not change routing graph edge costs.",
                "Suggested field checks require human or trusted-source verification before data updates."
            ]
        };
    }

    private static EdgePlanningFeatures BuildFeatures(RouteEdge edge, string profile)
    {
        var currentPenalty = ResolvePenalty(edge, profile);
        var repairedPenalty = EstimateCounterfactualPenalty(edge, profile);
        var penaltyReduction = Math.Max(0, currentPenalty - repairedPenalty);
        var dataGap = 1 - ResolveDataQuality(edge);
        var blockerScore = edge.HasStairs || edge.HasBarrier || currentPenalty >= 300
            ? 1
            : 0;
        var penaltyReductionPer100Metres = penaltyReduction / Math.Max(1, edge.DistanceMetres) * 100;
        var uncertaintyPenalty = Math.Clamp(dataGap * 35, 0, 35);
        var accessibilityAlpha = Math.Clamp(
            penaltyReductionPer100Metres - uncertaintyPenalty * 0.35 + blockerScore * 8,
            -50,
            100);
        var modelPrediction = ScoreWithModel(
            edge,
            dataGap,
            penaltyReductionPer100Metres,
            blockerScore);
        var hasActionableGap = dataGap > 0.05
                               || penaltyReduction > 0
                               || blockerScore > 0
                               || edge.KerbHeight > 0.03
                               || HasMissingSurface(edge)
                               || string.IsNullOrWhiteSpace(edge.Smoothness)
                               || !edge.WidthMetres.HasValue;
        var priority = Math.Clamp(
            hasActionableGap
                ? modelPrediction.Score * 55
                  + dataGap * 20
                  + Math.Min(15, penaltyReduction / 30)
                  + blockerScore * 8
                  + Math.Max(0, accessibilityAlpha) * 0.05
                : 0,
            0,
            100);
        var centroid = TryGetCentroid(edge);

        return new EdgePlanningFeatures(
            edge,
            centroid.Latitude,
            centroid.Longitude,
            currentPenalty,
            repairedPenalty,
            penaltyReduction,
            penaltyReductionPer100Metres,
            uncertaintyPenalty,
            accessibilityAlpha,
            modelPrediction,
            priority);
    }

    private static AccessibilityRepairModelPrediction ScoreWithModel(
        RouteEdge edge,
        double dataGap,
        double penaltyReductionPer100Metres,
        double blockerScore)
    {
        var model = RankerModel.Value;
        var features = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["dataGap"] = dataGap,
            ["penaltyReductionPer100Metres"] = Math.Min(500, Math.Max(0, penaltyReductionPer100Metres)),
            ["blockerScore"] = blockerScore,
            ["kerbRisk"] = edge.KerbHeight > 0.03 ? 1 : 0,
            ["missingWidth"] = edge.WidthMetres.HasValue ? 0 : 1,
            ["missingSmoothness"] = string.IsNullOrWhiteSpace(edge.Smoothness) ? 1 : 0,
            ["missingSurface"] = HasMissingSurface(edge) ? 1 : 0,
            ["distanceLog"] = Math.Log10(Math.Max(1, edge.DistanceMetres))
        };

        var logit = model.Intercept;
        var contributions = new List<AccessibilityRepairModelContribution>();
        foreach (var (feature, value) in features)
        {
            var weight = model.Weights.TryGetValue(feature, out var configuredWeight) ? configuredWeight : 0;
            var contribution = value * weight;
            logit += contribution;
            contributions.Add(new AccessibilityRepairModelContribution(feature, value, weight, contribution));
        }

        var score = 1.0 / (1.0 + Math.Exp(-logit));
        var confidence = score >= model.Calibration.High
            ? 0.9
            : score >= model.Calibration.Medium
                ? 0.75
                : score >= model.Calibration.Low
                    ? 0.6
                    : 0.45;

        return new AccessibilityRepairModelPrediction(
            model.Version,
            Math.Clamp(score, 0, 1),
            confidence,
            contributions
                .OrderByDescending(contribution => Math.Abs(contribution.Contribution))
                .ThenBy(contribution => contribution.Feature)
                .ToList());
    }

    private static AccessibilityRepairCandidate BuildCandidate(EdgePlanningFeatures features)
    {
        var edge = features.Edge;
        return new AccessibilityRepairCandidate
        {
            EdgeId = edge.Id,
            SourceWayId = edge.SourceWayId,
            Latitude = Math.Round(features.Latitude, 6),
            Longitude = Math.Round(features.Longitude, 6),
            DistanceMetres = Math.Round(edge.DistanceMetres, 2),
            CurrentDataQuality = Math.Round(ResolveDataQuality(edge), 4),
            CurrentPenaltySeconds = Math.Round(features.CurrentPenaltySeconds, 2),
            CounterfactualPenaltySeconds = Math.Round(features.CounterfactualPenaltySeconds, 2),
            EstimatedPenaltyReductionSeconds = Math.Round(features.EstimatedPenaltyReductionSeconds, 2),
            PenaltyReductionPer100Metres = Math.Round(features.PenaltyReductionPer100Metres, 2),
            DataUncertaintyPenalty = Math.Round(features.DataUncertaintyPenalty, 2),
            AccessibilityAlpha = Math.Round(features.AccessibilityAlpha, 2),
            ModelScore = Math.Round(features.ModelPrediction.Score, 4),
            ModelConfidence = Math.Round(features.ModelPrediction.Confidence, 4),
            ModelVersion = features.ModelPrediction.ModelVersion,
            FeatureContributions = features.ModelPrediction.Contributions
                .Select(contribution => new AccessibilityRepairFeatureContribution
                {
                    Feature = contribution.Feature,
                    Value = Math.Round(contribution.Value, 4),
                    Weight = Math.Round(contribution.Weight, 4),
                    Contribution = Math.Round(contribution.Contribution, 4)
                })
                .ToList(),
            PriorityScore = Math.Round(features.PriorityScore, 2),
            ReviewPriority = ResolveReviewPriority(features.PriorityScore),
            Reasons = BuildReasons(edge, features.CurrentPenaltySeconds, features.EstimatedPenaltyReductionSeconds),
            SuggestedFieldChecks = BuildSuggestedChecks(edge)
        };
    }

    private static List<AccessibilityPlanningFrontierPoint> BuildEfficientFrontier(
        IReadOnlyCollection<AccessibilityRepairCandidate> candidates)
    {
        return candidates
            .Where(candidate => candidate.EstimatedPenaltyReductionSeconds > 0)
            .OrderByDescending(candidate => candidate.AccessibilityAlpha)
            .ThenBy(candidate => candidate.DataUncertaintyPenalty)
            .ThenByDescending(candidate => candidate.PriorityScore)
            .Take(10)
            .Select(candidate => new AccessibilityPlanningFrontierPoint
            {
                EdgeId = candidate.EdgeId,
                DistanceMetres = candidate.DistanceMetres,
                ExpectedPenaltyReductionSeconds = candidate.EstimatedPenaltyReductionSeconds,
                AccessibilityAlpha = candidate.AccessibilityAlpha,
                DataUncertaintyPenalty = candidate.DataUncertaintyPenalty,
                PriorityScore = candidate.PriorityScore
            })
            .ToList();
    }

    private static string ResolveReviewPriority(double priorityScore) => priorityScore switch
    {
        >= 75 => "critical",
        >= 50 => "high",
        >= 25 => "medium",
        _ => "low"
    };

    private static double EstimateCounterfactualPenalty(RouteEdge edge, string profile)
    {
        var repairedSurface = HasMissingSurface(edge) ? "asphalt" : edge.SurfaceType;
        var repairedSmoothness = string.IsNullOrWhiteSpace(edge.Smoothness) ? "good" : edge.Smoothness;
        var repairedWidth = edge.WidthMetres ?? 1.5;
        var repairedKerb = edge.KerbHeight > 0.03 ? 0.02 : edge.KerbHeight;
        var repairedBarrier = false;
        var repairedStairs = false;
        var repairedAccess = IsAccessBlocked(edge.Access) ? string.Empty : edge.Access;

        return RouteEdgeCostModel.ComputePenaltySeconds(
            edge.DistanceMetres,
            repairedSurface,
            repairedSmoothness,
            repairedStairs,
            repairedBarrier,
            repairedKerb,
            repairedWidth,
            edge.IsSteep,
            repairedAccess,
            profile);
    }

    private static List<string> BuildReasons(RouteEdge edge, double currentPenalty, double penaltyReduction)
    {
        var reasons = new List<string>();

        if (HasMissingSurface(edge)) reasons.Add("missing surface tag");
        if (string.IsNullOrWhiteSpace(edge.Smoothness)) reasons.Add("missing smoothness tag");
        if (!edge.WidthMetres.HasValue) reasons.Add("missing width tag");
        if (edge.HasStairs) reasons.Add("stairs block strict accessibility profiles");
        if (edge.HasBarrier) reasons.Add("barrier blocks strict accessibility profiles");
        if (edge.KerbHeight > 0.03) reasons.Add("kerb height likely needs ramp verification");
        if (IsAccessBlocked(edge.Access)) reasons.Add("access tag blocks traversal");
        if (currentPenalty >= 300) reasons.Add("high current accessibility penalty");
        if (penaltyReduction > 0) reasons.Add("counterfactual repair reduces traversal penalty");

        return reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> BuildSuggestedChecks(RouteEdge edge)
    {
        var checks = new List<string>();

        if (HasMissingSurface(edge)) checks.Add("verify surface material");
        if (string.IsNullOrWhiteSpace(edge.Smoothness)) checks.Add("verify wheel smoothness");
        if (!edge.WidthMetres.HasValue) checks.Add("measure clear path width");
        if (edge.KerbHeight > 0.03) checks.Add("measure kerb height and ramp presence");
        if (edge.HasStairs || edge.HasBarrier || IsAccessBlocked(edge.Access)) checks.Add("confirm accessible alternative or removable blocker");

        return checks.Count == 0 ? ["review local accessibility condition"] : checks;
    }

    private static PlanningBounds NormalizeBounds(AccessibilityPlanningRequest request)
    {
        var minLat = Math.Min(request.MinLatitude, request.MaxLatitude);
        var maxLat = Math.Max(request.MinLatitude, request.MaxLatitude);
        var minLon = Math.Min(request.MinLongitude, request.MaxLongitude);
        var maxLon = Math.Max(request.MinLongitude, request.MaxLongitude);

        if (minLat < -90 || maxLat > 90 || minLon < -180 || maxLon > 180 || minLat == maxLat || minLon == maxLon)
        {
            throw new ArgumentException("Invalid bounding box.");
        }

        return new PlanningBounds(minLat, minLon, maxLat, maxLon);
    }

    private static bool IsInsideBounds(RouteEdge edge, PlanningBounds bounds)
    {
        if (edge.Geometry is null)
        {
            return false;
        }

        return edge.Geometry.Coordinates.Any(coordinate =>
            coordinate.Y >= bounds.MinLatitude
            && coordinate.Y <= bounds.MaxLatitude
            && coordinate.X >= bounds.MinLongitude
            && coordinate.X <= bounds.MaxLongitude);
    }

    private static Polygon BuildSearchArea(PlanningBounds bounds)
    {
        var ring = new LinearRing(
        [
            new Coordinate(bounds.MinLongitude, bounds.MinLatitude),
            new Coordinate(bounds.MaxLongitude, bounds.MinLatitude),
            new Coordinate(bounds.MaxLongitude, bounds.MaxLatitude),
            new Coordinate(bounds.MinLongitude, bounds.MaxLatitude),
            new Coordinate(bounds.MinLongitude, bounds.MinLatitude)
        ]);

        return new Polygon(ring) { SRID = 4326 };
    }

    private static (double Latitude, double Longitude) TryGetCentroid(RouteEdge edge)
    {
        if (edge.Geometry?.Coordinates is { Length: > 0 } coordinates)
        {
            return (coordinates.Average(c => c.Y), coordinates.Average(c => c.X));
        }

        return (0, 0);
    }

    private static string NormalizeProfile(string? profile) =>
        string.IsNullOrWhiteSpace(profile) ? "manual-wheelchair" : profile.Trim().ToLowerInvariant();

    private static double ResolveDataQuality(RouteEdge edge) =>
        edge.AccessibilityDataQuality > 0
            ? Math.Clamp(edge.AccessibilityDataQuality, 0, 1)
            : RouteEdgeCostModel.ComputeAccessibilityDataQuality(edge.SurfaceType, edge.Smoothness, edge.WidthMetres);

    private static double ResolvePenalty(RouteEdge edge, string profile)
    {
        var profilePenalty = RouteEdgeCostModel.ResolvePenaltySeconds(ToGraphEdge(edge), profile);
        return Math.Max(0, profilePenalty);
    }

    private static GraphEdge ToGraphEdge(RouteEdge edge) => new()
    {
        DistanceMetres = edge.DistanceMetres,
        SurfaceType = edge.SurfaceType,
        Smoothness = edge.Smoothness,
        HasStairs = edge.HasStairs,
        HasBarrier = edge.HasBarrier,
        KerbHeight = edge.KerbHeight,
        WidthMetres = edge.WidthMetres,
        IsSteep = edge.IsSteep,
        Access = edge.Access,
        AccessibilityCostVersion = edge.AccessibilityCostVersion,
        StandardAccessibilityPenaltySeconds = edge.StandardAccessibilityPenaltySeconds,
        WheelchairAccessibilityPenaltySeconds = edge.WheelchairAccessibilityPenaltySeconds,
        StrollerAccessibilityPenaltySeconds = edge.StrollerAccessibilityPenaltySeconds
    };

    private static bool HasMissingSurface(RouteEdge edge) =>
        string.IsNullOrWhiteSpace(edge.SurfaceType)
        || string.Equals(edge.SurfaceType, "unknown", StringComparison.OrdinalIgnoreCase);

    private static bool IsAccessBlocked(string? access)
    {
        if (string.IsNullOrWhiteSpace(access))
        {
            return false;
        }

        var normalized = access.ToLowerInvariant();
        return normalized.Contains("access=no", StringComparison.Ordinal)
               || normalized.Contains("access=private", StringComparison.Ordinal)
               || normalized.Contains("foot=no", StringComparison.Ordinal)
               || normalized.Contains("wheelchair=no", StringComparison.Ordinal);
    }

    private static double Average(IReadOnlyCollection<RouteEdge> edges, Func<RouteEdge, double> selector) =>
        edges.Count == 0 ? 0 : edges.Average(selector);

    private static double Share(IReadOnlyCollection<RouteEdge> edges, Func<RouteEdge, bool> predicate) =>
        edges.Count == 0 ? 0 : edges.Count(predicate) / (double)edges.Count;

    private static AccessibilityRepairRankerModel LoadRankerModel()
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Models", "accessibility_repair_ranker_v1.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "AccessCity.API", "Models", "accessibility_repair_ranker_v1.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Models", "accessibility_repair_ranker_v1.json")
        };
        var path = candidatePaths.FirstOrDefault(File.Exists)
                   ?? throw new FileNotFoundException(
                       "Accessibility repair ranker model file was not found.",
                       candidatePaths[0]);
        var model = JsonSerializer.Deserialize<AccessibilityRepairRankerModel>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return model ?? throw new InvalidOperationException("Accessibility repair ranker model could not be parsed.");
    }

    private readonly record struct PlanningBounds(
        double MinLatitude,
        double MinLongitude,
        double MaxLatitude,
        double MaxLongitude);

    private readonly record struct EdgePlanningFeatures(
        RouteEdge Edge,
        double Latitude,
        double Longitude,
        double CurrentPenaltySeconds,
        double CounterfactualPenaltySeconds,
        double EstimatedPenaltyReductionSeconds,
        double PenaltyReductionPer100Metres,
        double DataUncertaintyPenalty,
        double AccessibilityAlpha,
        AccessibilityRepairModelPrediction ModelPrediction,
        double PriorityScore);

    private sealed record AccessibilityRepairRankerModel(
        string Version,
        string Kind,
        double Intercept,
        Dictionary<string, double> Weights,
        AccessibilityRepairModelCalibration Calibration,
        List<string> Guardrails);

    private sealed record AccessibilityRepairModelCalibration(double Low, double Medium, double High);

    private sealed record AccessibilityRepairModelContribution(
        string Feature,
        double Value,
        double Weight,
        double Contribution);

    private sealed record AccessibilityRepairModelPrediction(
        string ModelVersion,
        double Score,
        double Confidence,
        IReadOnlyList<AccessibilityRepairModelContribution> Contributions);
}
