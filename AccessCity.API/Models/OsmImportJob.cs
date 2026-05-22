using System.Text.Json;

namespace AccessCity.API.Models;

public sealed class OsmImportJob
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "queued";
    public string FilePath { get; set; } = string.Empty;
    public string CityName { get; set; } = "configured";
    public DateTime QueuedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public int Attempts { get; set; }
    public long? FeedIngestionRunId { get; set; }
    public string? ErrorSummary { get; set; }
    public JsonDocument Metadata { get; set; } = JsonDocument.Parse("{}");
}
