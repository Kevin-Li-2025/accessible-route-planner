namespace AccessCity.API.Models;

public sealed class InfrastructureAccessibilityProfile
{
    public string SchemaVersion { get; init; } = "accessibility-profile.v1";
    public string SourceSystem { get; init; } = string.Empty;
    public string? SourceRecordId { get; init; }
    public DateTime ProfileGeneratedAtUtc { get; init; }
    public DateTime? LastVerifiedAtUtc { get; init; }
    public string VerificationStatus { get; init; } = "unverified";
    public double Confidence { get; init; }
    public AccessibilityPathAttributes Path { get; init; } = new();
    public List<AccessibilityEntrance> Entrances { get; init; } = [];
    public List<AccessibilityRestroom> Restrooms { get; init; } = [];
    public List<AccessibilityPhoto> Photos { get; init; } = [];
    public List<string> MissingFields { get; init; } = [];
    public Dictionary<string, string> EvidenceTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int RawTagCount { get; init; }
}

public sealed class AccessibilityPathAttributes
{
    public string? Surface { get; init; }
    public string? Smoothness { get; init; }
    public double? WidthMetres { get; init; }
    public double? KerbHeightMetres { get; init; }
    public double? InclinePercent { get; init; }
    public string? InclineText { get; init; }
    public bool? HasTactilePaving { get; init; }
    public bool? HasCurbRamp { get; init; }
    public bool? HasStepFreeAccess { get; init; }
    public bool? HasStairs { get; init; }
    public bool? HasBarrier { get; init; }
    public string? WheelchairAccess { get; init; }
    public string? Lighting { get; init; }
    public string? CrossingType { get; init; }
    public string? Access { get; init; }
}

public sealed class AccessibilityEntrance
{
    public string? Name { get; init; }
    public string? EntranceType { get; init; }
    public bool? StepFree { get; init; }
    public bool? HasRamp { get; init; }
    public double? DoorWidthMetres { get; init; }
    public bool? AutomaticDoor { get; init; }
    public double? StepHeightMetres { get; init; }
}

public sealed class AccessibilityRestroom
{
    public bool? WheelchairAccessible { get; init; }
    public bool? HasGrabBars { get; init; }
    public double? DoorWidthMetres { get; init; }
    public double? TurningSpaceMetres { get; init; }
    public bool? HasChangingTable { get; init; }
    public bool? RequiresKey { get; init; }
    public string? GenderAccess { get; init; }
}

public sealed class AccessibilityPhoto
{
    public string Source { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string? Caption { get; init; }
    public DateTime? TakenAtUtc { get; init; }
    public string VerificationStatus { get; init; } = "unverified";
}
