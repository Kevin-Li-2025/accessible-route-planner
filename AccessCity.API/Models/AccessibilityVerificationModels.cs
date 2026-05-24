using System.Text.Json;

namespace AccessCity.API.Models;

public sealed class AccessibilityVerificationSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long InfrastructureAssetId { get; set; }
    public InfrastructureAsset? InfrastructureAsset { get; set; }
    public string SubmittedByUserId { get; set; } = string.Empty;
    public string Source { get; set; } = "field_report";
    public string Status { get; set; } = AccessibilityVerificationStatus.Pending;
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ObservedAtUtc { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? ReviewedByUserId { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public string? Notes { get; set; }
    public double Confidence { get; set; }
    public JsonDocument AttributeUpdates { get; set; } = JsonDocument.Parse("{}");
    public JsonDocument PhotoUrls { get; set; } = JsonDocument.Parse("[]");
}

public static class AccessibilityVerificationStatus
{
    public const string Pending = "pending";
    public const string Applied = "applied";
    public const string Rejected = "rejected";
}

public sealed class AccessibilityVerificationRequest
{
    public DateTime? ObservedAtUtc { get; set; }
    public string Source { get; set; } = "field_report";
    public string? Notes { get; set; }
    public AccessibilityPathAttributes? Path { get; set; }
    public AccessibilityEntrance? Entrance { get; set; }
    public AccessibilityRestroom? Restroom { get; set; }
    public List<AccessibilityPhotoInput> Photos { get; set; } = [];
}

public sealed class AccessibilityPhotoInput
{
    public string Source { get; set; } = "field_photo";
    public string Url { get; set; } = string.Empty;
    public string? Caption { get; set; }
    public DateTime? TakenAtUtc { get; set; }
}

public sealed class AccessibilityVerificationResponse
{
    public Guid Id { get; set; }
    public long InfrastructureAssetId { get; set; }
    public string Status { get; set; } = AccessibilityVerificationStatus.Pending;
    public string Source { get; set; } = "field_report";
    public DateTime SubmittedAtUtc { get; set; }
    public DateTime? ObservedAtUtc { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public double Confidence { get; set; }
    public string? Notes { get; set; }
    public List<string> UpdatedFields { get; set; } = [];
    public List<string> PhotoUrls { get; set; } = [];
    public InfrastructureAccessibilityProfile? AccessibilityProfile { get; set; }
}

public sealed class AccessibilityVerificationReviewRequest
{
    public string? Notes { get; set; }
}
