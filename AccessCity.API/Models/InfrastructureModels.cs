using System.Text.Json;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Models;

public class InfrastructureAsset
{
    public long Id { get; set; }
    public string AssetType { get; set; } = string.Empty;
    public string? Name { get; set; }
    public Geometry Geometry { get; set; } = null!;
    public string Status { get; set; } = "active";
    public JsonDocument AccessibilityInfo { get; set; } = JsonDocument.Parse("{}");
    public string SourceSystem { get; set; } = string.Empty;
    public string? SourceRecordId { get; set; }
    public DateTime? LastObservedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class FeedIngestionRun
{
    public long Id { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public int RecordsSeen { get; set; }
    public int RecordsInserted { get; set; }
    public int RecordsUpdated { get; set; }
    public int RecordsFailed { get; set; }
    public string? ErrorSummary { get; set; }
    public JsonDocument Metadata { get; set; } = JsonDocument.Parse("{}");
}

public sealed class OsmImportResult
{
    public long RunId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int RecordsSeen { get; set; }
    public int RouteNodesInserted { get; set; }
    public int RouteEdgesInserted { get; set; }
    public int InfrastructureAssetsInserted { get; set; }
    public int RecordsFailed { get; set; }
    public TimeSpan Duration { get; set; }
}
