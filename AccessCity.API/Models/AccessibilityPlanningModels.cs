namespace AccessCity.API.Models;

public sealed class AccessibilityPlanningRequest
{
    public double MinLatitude { get; set; }
    public double MinLongitude { get; set; }
    public double MaxLatitude { get; set; }
    public double MaxLongitude { get; set; }
    public string Profile { get; set; } = "manual-wheelchair";
    public int MaxCandidates { get; set; } = 20;
}

public sealed class AccessibilityDataQualitySummary
{
    public DateTime GeneratedAtUtc { get; set; }
    public string Profile { get; set; } = "manual-wheelchair";
    public string RankingModelVersion { get; set; } = string.Empty;
    public string RankingModelKind { get; set; } = string.Empty;
    public int EdgeCount { get; set; }
    public double TotalDistanceMetres { get; set; }
    public double AverageDataQuality { get; set; }
    public double DistanceWeightedDataQuality { get; set; }
    public double MissingSurfaceShare { get; set; }
    public double MissingSmoothnessShare { get; set; }
    public double MissingWidthShare { get; set; }
    public double BarrierOrStairsShare { get; set; }
    public double HighPenaltyShare { get; set; }
    public List<AccessibilityRepairCandidate> RepairCandidates { get; set; } = new();
    public List<AccessibilityPlanningFrontierPoint> EfficientFrontier { get; set; } = new();
    public List<string> Guardrails { get; set; } = new();
}

public sealed class AccessibilityRepairCandidate
{
    public long EdgeId { get; set; }
    public long? SourceWayId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double DistanceMetres { get; set; }
    public double CurrentDataQuality { get; set; }
    public double CurrentPenaltySeconds { get; set; }
    public double CounterfactualPenaltySeconds { get; set; }
    public double EstimatedPenaltyReductionSeconds { get; set; }
    public double PenaltyReductionPer100Metres { get; set; }
    public double DataUncertaintyPenalty { get; set; }
    public double AccessibilityAlpha { get; set; }
    public double ModelScore { get; set; }
    public double ModelConfidence { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    public List<AccessibilityRepairFeatureContribution> FeatureContributions { get; set; } = new();
    public double PriorityScore { get; set; }
    public string ReviewPriority { get; set; } = "medium";
    public List<string> Reasons { get; set; } = new();
    public List<string> SuggestedFieldChecks { get; set; } = new();
}

public sealed class AccessibilityRepairFeatureContribution
{
    public string Feature { get; set; } = string.Empty;
    public double Value { get; set; }
    public double Weight { get; set; }
    public double Contribution { get; set; }
}

public sealed class AccessibilityPlanningFrontierPoint
{
    public long EdgeId { get; set; }
    public double DistanceMetres { get; set; }
    public double ExpectedPenaltyReductionSeconds { get; set; }
    public double AccessibilityAlpha { get; set; }
    public double DataUncertaintyPenalty { get; set; }
    public double PriorityScore { get; set; }
}
