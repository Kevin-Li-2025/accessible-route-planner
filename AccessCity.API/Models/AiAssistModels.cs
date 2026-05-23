namespace AccessCity.API.Models;

public sealed class HazardAiEnrichmentResult
{
    public Guid HazardId { get; set; }
    public bool ForRouteDecision { get; set; }
    public string Provider { get; set; } = "local-rules";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public HazardTextEnrichment Text { get; set; } = new();
    public List<DuplicateHazardSuggestion> DuplicateSuggestions { get; set; } = new();
    public List<MissingOsmAttributeCandidate> MissingOsmAttributeCandidates { get; set; } = new();
    public List<string> Guardrails { get; set; } = new();
}

public sealed class HazardTextEnrichment
{
    public string NormalizedDescription { get; set; } = string.Empty;
    public string SuggestedType { get; set; } = string.Empty;
    public string SuggestedSeverity { get; set; } = "medium";
    public double Confidence { get; set; }
    public string AdminSummary { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
}

public sealed class DuplicateHazardSuggestion
{
    public Guid HazardId { get; set; }
    public double DistanceMetres { get; set; }
    public double Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class MissingOsmAttributeCandidate
{
    public string Attribute { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Evidence { get; set; } = string.Empty;
    public string Source { get; set; } = "user_report_text";
    public bool CanAutoApply { get; set; }
}

public sealed class RouteExplanationRequest
{
    public RouteRequest RouteRequest { get; set; } = new();
    public RouteResponse Route { get; set; } = new();
}

public sealed class RouteExplanationResponse
{
    public bool ForRouteDecision { get; set; }
    public string Provider { get; set; } = "local-rules";
    public string Explanation { get; set; } = string.Empty;
    public List<string> Reasons { get; set; } = new();
    public List<string> Limitations { get; set; } = new();
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
}
