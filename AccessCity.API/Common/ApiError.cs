namespace AccessCity.API.Common;

/// <summary>
/// Standardised API error envelope returned by all endpoints for consistent client-side handling.
/// </summary>
public sealed record ApiError(
    string Error,
    string? CorrelationId = null,
    string? Detail = null,
    int? UpstreamStatus = null);
