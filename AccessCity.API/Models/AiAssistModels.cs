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

public sealed class HazardReportDraftAiRequest
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Type { get; set; } = "other";
    public string Description { get; set; } = string.Empty;
    public bool PhotoAttached { get; set; }
    public string? PhotoUrl { get; set; }
}

public sealed class HazardReportDraftAiResult
{
    public bool ForRouteDecision { get; set; }
    public string Provider { get; set; } = "local-rules";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public HazardTextEnrichment Text { get; set; } = new();
    public List<DuplicateHazardSuggestion> DuplicateSuggestions { get; set; } = new();
    public List<MissingOsmAttributeCandidate> MissingOsmAttributeCandidates { get; set; } = new();
    public bool ShouldReviewExistingReport { get; set; }
    public List<string> SuggestedDescriptionChips { get; set; } = new();
    public List<string> Guardrails { get; set; } = new();
}

public sealed class HazardPhotoAiAnalysisRequest
{
    public string? PhotoUrl { get; set; }
    public string? ObservationText { get; set; }
    public bool IncludeDraftVerification { get; set; } = true;
    public bool SubmitForReview { get; set; } = true;
    public double MaxAssetDistanceMetres { get; set; } = 35;
}

public sealed class HazardPhotoAiAnalysisResult
{
    public Guid HazardId { get; set; }
    public long? LinkedInfrastructureAssetId { get; set; }
    public bool ForRouteDecision { get; set; }
    public string Provider { get; set; } = "local-rules";
    public string Model { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string PhotoUrl { get; set; } = string.Empty;
    public string ReviewStatus { get; set; } = "review_required";
    public string AdminSummary { get; set; } = string.Empty;
    public List<MissingOsmAttributeCandidate> AttributeCandidates { get; set; } = new();
    public AccessibilityVerificationRequest? DraftVerification { get; set; }
    public AccessibilityVerificationResponse? ReviewSubmission { get; set; }
    public List<string> Guardrails { get; set; } = new();
    public List<string> Limitations { get; set; } = new();
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

public sealed class AccessibilityAiReviewResult
{
    public long InfrastructureAssetId { get; set; }
    public bool ForRouteDecision { get; set; }
    public string Provider { get; set; } = "local-rules";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string AdminSummary { get; set; } = string.Empty;
    public List<MissingOsmAttributeCandidate> MissingAttributeCandidates { get; set; } = new();
    public List<string> VerificationChecklist { get; set; } = new();
    public List<string> Guardrails { get; set; } = new();
}

public sealed class AccessibilityAiInferenceRequest
{
    public string ObservationText { get; set; } = string.Empty;
    public List<AccessibilityPhotoInput> Photos { get; set; } = new();
    public bool IncludeDraftVerification { get; set; } = true;
}

public sealed class AccessibilityAiInferenceResult
{
    public long InfrastructureAssetId { get; set; }
    public bool ForRouteDecision { get; set; }
    public string Provider { get; set; } = "local-rules";
    public string Model { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string AdminSummary { get; set; } = string.Empty;
    public List<MissingOsmAttributeCandidate> AttributeCandidates { get; set; } = new();
    public AccessibilityVerificationRequest? DraftVerification { get; set; }
    public List<string> Guardrails { get; set; } = new();
    public List<string> Limitations { get; set; } = new();
}
