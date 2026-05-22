namespace AccessCity.API.Models.DTOs;

public sealed record OsmImportJobResponse(
    Guid JobId,
    string Status,
    string FilePath,
    DateTime QueuedAtUtc,
    DateTime? StartedAtUtc = null,
    DateTime? FinishedAtUtc = null,
    int Attempts = 0,
    long? FeedIngestionRunId = null,
    string? ErrorSummary = null);
