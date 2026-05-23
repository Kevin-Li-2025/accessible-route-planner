using AccessCity.API.Models;

namespace AccessCity.API.Services;

public static class RouteEdgeCostModel
{
    public const int Version = 1;
    public const int EdgeWeightVersion = 1;

    public static RouteEdgeCostProfile Compute(
        double distanceMetres,
        string? surface,
        string? smoothness,
        bool hasStairs,
        bool hasBarrier,
        double kerbHeight,
        double? widthMetres,
        bool isSteep,
        string? access)
    {
        return new RouteEdgeCostProfile(
            Version,
            ComputePenaltySeconds(distanceMetres, surface, smoothness, hasStairs, hasBarrier, kerbHeight, widthMetres, isSteep, access, "standard"),
            ComputePenaltySeconds(distanceMetres, surface, smoothness, hasStairs, hasBarrier, kerbHeight, widthMetres, isSteep, access, "manual-wheelchair"),
            ComputePenaltySeconds(distanceMetres, surface, smoothness, hasStairs, hasBarrier, kerbHeight, widthMetres, isSteep, access, "stroller"),
            ComputeAccessibilityDataQuality(surface, smoothness, widthMetres));
    }

    public static double ResolvePenaltySeconds(GraphEdge edge, string? profile)
    {
        if (edge.AccessibilityCostVersion >= Version)
        {
            return profile?.ToLowerInvariant() switch
            {
                "manual-wheelchair" or "power-wheelchair" => edge.WheelchairAccessibilityPenaltySeconds,
                "stroller" => edge.StrollerAccessibilityPenaltySeconds,
                _ => edge.StandardAccessibilityPenaltySeconds
            };
        }

        return ComputePenaltySeconds(
            edge.DistanceMetres,
            edge.SurfaceType,
            edge.Smoothness,
            edge.HasStairs,
            edge.HasBarrier,
            edge.KerbHeight,
            edge.WidthMetres,
            edge.IsSteep,
            edge.Access,
            profile);
    }

    public static void PopulateTraversalWeights(GraphEdge edge)
    {
        edge.EdgeWeightVersion = EdgeWeightVersion;
        edge.StandardTraversalSeconds = ComputeTraversalSeconds(edge.DistanceMetres, edge.StandardAccessibilityPenaltySeconds, "standard");
        edge.WheelchairTraversalSeconds = ComputeTraversalSeconds(edge.DistanceMetres, edge.WheelchairAccessibilityPenaltySeconds, "manual-wheelchair");
        edge.StrollerTraversalSeconds = ComputeTraversalSeconds(edge.DistanceMetres, edge.StrollerAccessibilityPenaltySeconds, "stroller");
    }

    public static double ResolveTraversalSeconds(GraphEdge edge, string? profile)
    {
        if (edge.EdgeWeightVersion >= EdgeWeightVersion)
        {
            return profile?.ToLowerInvariant() switch
            {
                "manual-wheelchair" or "power-wheelchair" => edge.WheelchairTraversalSeconds,
                "stroller" => edge.StrollerTraversalSeconds,
                _ => edge.StandardTraversalSeconds
            };
        }

        return ComputeTraversalSeconds(edge.DistanceMetres, ResolvePenaltySeconds(edge, profile), profile);
    }

    public static double ComputeTraversalSeconds(double distanceMetres, double accessibilityPenaltySeconds, string? profile) =>
        distanceMetres / ResolveProfileSpeed(profile) + Math.Max(0, accessibilityPenaltySeconds);

    public static double ComputePenaltySeconds(
        double distanceMetres,
        string? surface,
        string? smoothness,
        bool hasStairs,
        bool hasBarrier,
        double kerbHeight,
        double? widthMetres,
        bool isSteep,
        string? access,
        string? profile)
    {
        var strict = IsAccessibilityProfile(profile);
        var normalizedSurface = string.IsNullOrWhiteSpace(surface) ? "unknown" : surface.Trim().ToLowerInvariant();
        var baseSeconds = distanceMetres / ResolveProfileSpeed(profile);
        double penalty = 0;

        if (hasStairs) penalty += strict ? 600 : 90;
        if (hasBarrier) penalty += strict ? 600 : 60;
        if (kerbHeight > 0.03) penalty += strict ? 300 : 30;
        if (IsAccessBlocked(access)) penalty += strict ? 600 : 90;

        if (normalizedSurface is "unknown") penalty += baseSeconds * (strict ? 0.6 : 0.15);
        if (normalizedSurface is "cobblestone" or "sett") penalty += baseSeconds * (strict ? 2.0 : 0.4);
        if (normalizedSurface is "gravel" or "unpaved" or "sand" or "dirt" or "earth" or "grass")
        {
            penalty += baseSeconds * (strict ? 4.0 : 0.8);
        }

        if (!SmoothnessAllowsWheels(smoothness)) penalty += strict ? 300 : 45;
        else if (strict && string.IsNullOrWhiteSpace(smoothness)) penalty += baseSeconds * 0.25;

        if (widthMetres.HasValue && widthMetres < 0.9) penalty += strict ? 300 : 30;
        else if (strict && !widthMetres.HasValue) penalty += baseSeconds * 0.35;

        if (isSteep) penalty += baseSeconds * (strict ? 1.5 : 0.5);

        return Math.Clamp(penalty, 0, 2400);
    }

    public static double ComputeAccessibilityDataQuality(string? surface, string? smoothness, double? widthMetres)
    {
        double quality = 1.0;
        if (string.IsNullOrWhiteSpace(surface) || string.Equals(surface, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            quality -= 0.25;
        }

        if (string.IsNullOrWhiteSpace(smoothness))
        {
            quality -= 0.20;
        }

        if (!widthMetres.HasValue)
        {
            quality -= 0.25;
        }

        return Math.Clamp(quality, 0.10, 1.0);
    }

    private static double ResolveProfileSpeed(string? profile) => profile switch
    {
        "manual-wheelchair" => 0.9,
        "power-wheelchair" => 1.1,
        "stroller" => 1.1,
        _ => 1.3
    };

    private static bool IsAccessibilityProfile(string? profile) =>
        string.Equals(profile, "manual-wheelchair", StringComparison.OrdinalIgnoreCase)
        || string.Equals(profile, "power-wheelchair", StringComparison.OrdinalIgnoreCase)
        || string.Equals(profile, "stroller", StringComparison.OrdinalIgnoreCase);

    private static bool SmoothnessAllowsWheels(string? smoothness)
    {
        if (string.IsNullOrWhiteSpace(smoothness))
        {
            return true;
        }

        return smoothness.ToLowerInvariant() switch
        {
            "bad" or "very_bad" or "horrible" or "very_horrible" or "impassable" => false,
            _ => true
        };
    }

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
}

public readonly record struct RouteEdgeCostProfile(
    int Version,
    double StandardAccessibilityPenaltySeconds,
    double WheelchairAccessibilityPenaltySeconds,
    double StrollerAccessibilityPenaltySeconds,
    double AccessibilityDataQuality);
