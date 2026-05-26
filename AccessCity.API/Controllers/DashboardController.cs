using AccessCity.API.Security;
using AccessCity.API.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AccessCity.API.Controllers;

/// <summary>
/// Dashboard analytics: hazard summaries, heat-map GeoJSON, and infrastructure feeds.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[EnableRateLimiting(AccessCityRateLimitPolicies.HotRead)]
[RequestTimeout(AccessCityRequestTimeoutPolicies.ShortRead)]
public class DashboardController : ControllerBase
{
    private readonly IDashboardQueryService _dashboard;

    public DashboardController(IDashboardQueryService dashboard)
    {
        _dashboard = dashboard;
    }

    /// <summary>
    /// Returns dashboard summary from real OSM hazard data: total hazards, pending, resolved, active users.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken = default)
    {
        return Ok(await _dashboard.GetSummaryAsync(cancellationToken));
    }

    /// <summary>
    /// Returns GeoJSON FeatureCollection of real hazard locations (OSM) for heat-map rendering.
    /// </summary>
    [HttpGet("heat-map")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHeatMap(CancellationToken cancellationToken = default)
    {
        return Ok(await _dashboard.GetHeatMapAsync(cancellationToken));
    }

    /// <summary>
    /// Returns recent hazards from real OSM data as an infrastructure feed (newest first).
    /// </summary>
    [HttpGet("infrastructure-feed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInfrastructureFeed(
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _dashboard.GetInfrastructureFeedAsync(limit, cancellationToken));
    }
}
