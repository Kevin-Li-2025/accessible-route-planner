using AccessCity.API.Common;
using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AccessCity.API.Controllers;

/// <summary>
/// Dashboard analytics: hazard summaries, heat-map GeoJSON, and infrastructure feeds.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly IRealHazardDataService _realHazardData;
    private readonly AppDbContext _db;

    public DashboardController(IRealHazardDataService realHazardData, AppDbContext db)
    {
        _realHazardData = realHazardData;
        _db = db;
    }

    /// <summary>
    /// Returns dashboard summary from real OSM hazard data: total hazards, pending, resolved, active users.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken = default)
    {
        var hazards = await _realHazardData.GetActiveHazardsAsync();
        var totalHazards = hazards.Count;
        var pendingAlerts = hazards.Count(h =>
            h.Status == HazardStatus.Reported || h.Status == HazardStatus.UnderReview);

        var now = DateTime.UtcNow;
        var activeUsers = await _db.RefreshTokens.AsNoTracking()
            .Where(t => t.Revoked == null && t.Expires > now)
            .Select(t => t.UserId)
            .Distinct()
            .CountAsync(cancellationToken);

        return Ok(new
        {
            TotalHazards = totalHazards,
            ActiveUsers = activeUsers,
            ActiveUsersDefinition = "Distinct accounts with at least one non-revoked, non-expired refresh token.",
            PendingAlerts = pendingAlerts,
            Resolved = hazards.Count(h => h.Status == HazardStatus.Resolved),
        });
    }

    /// <summary>
    /// Returns GeoJSON FeatureCollection of real hazard locations (OSM) for heat-map rendering.
    /// </summary>
    [HttpGet("heat-map")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHeatMap(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken; // Propagation point for future async data sources.
        var hazards = await _realHazardData.GetActiveHazardsAsync();
        var features = new List<object>();

        foreach (var h in hazards)
        {
            if (h.Location == null) continue;

            var coords = new[] { h.Location.X, h.Location.Y };
            features.Add(new
            {
                type = "Feature",
                geometry = new
                {
                    type = "Point",
                    coordinates = coords,
                },
                properties = new
                {
                    id = h.Id,
                    type = h.Type,
                    status = h.Status.ToString(),
                    reportedAt = h.ReportedAt,
                },
            });
        }

        return Ok(new
        {
            type = "FeatureCollection",
            features,
        });
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
        _ = cancellationToken;
        var hazards = await _realHazardData.GetActiveHazardsAsync();
        var feed = hazards
            .OrderByDescending(h => h.ReportedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(h => new
            {
                id = h.Id,
                type = h.Type,
                description = h.Description,
                status = h.Status.ToString(),
                reportedAt = h.ReportedAt,
                coordinates = h.Location != null
                    ? new[] { h.Location.X, h.Location.Y }
                    : (double[]?)null,
            })
            .ToList();

        return Ok(feed);
    }
}
