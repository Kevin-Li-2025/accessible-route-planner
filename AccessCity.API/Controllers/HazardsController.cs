using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Asp.Versioning;
using AccessCity.API.Common;
using AccessCity.API.Data;
using AccessCity.API.Hubs;
using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using AccessCity.API.Services;

namespace AccessCity.API.Controllers;

/// <summary>
/// CRUD operations for hazard reports with real-time SignalR broadcasting.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public class HazardsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ISpatialCacheService _spatialCache;
    private readonly IRealHazardDataService _realHazardData;
    private readonly IHubContext<HazardAlertHub> _alertHub;

    public HazardsController(
        AppDbContext dbContext,
        ISpatialCacheService spatialCache,
        IRealHazardDataService realHazardData,
        IHubContext<HazardAlertHub> alertHub)
    {
        _dbContext = dbContext;
        _spatialCache = spatialCache;
        _realHazardData = realHazardData;
        _alertHub = alertHub;
    }

    /// <summary>
    /// Lists hazard reports, optionally filtered by bounding box and status.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<HazardReport>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<HazardReport>>> GetHazards(
        [FromQuery] double? minLat,
        [FromQuery] double? minLng,
        [FromQuery] double? maxLat,
        [FromQuery] double? maxLng,
        [FromQuery] HazardStatus? status,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var hazards = await _realHazardData.GetActiveHazardsAsync(minLat, minLng, maxLat, maxLng, status);
        return Ok(hazards);
    }

    /// <summary>
    /// Creates a new hazard report and broadcasts a real-time alert via SignalR.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(HazardReport), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HazardReport>> ReportHazard(
        [FromBody] CreateHazardRequest request,
        CancellationToken cancellationToken = default)
    {
        var report = new HazardReport
        {
            Id = Guid.NewGuid(),
            Location = new Point(request.Location) { SRID = 4326 },
            Type = request.Type.Trim(),
            Description = request.Description.Trim(),
            PhotoUrl = request.PhotoUrl ?? string.Empty,
            ReportedAt = DateTime.UtcNow,
            Status = HazardStatus.Reported,
            Source = string.IsNullOrWhiteSpace(request.Source) ? "user" : request.Source
        };

        if (report.Type.Length > 50) report.Type = report.Type[..50];
        if (report.Description.Length > 500) report.Description = report.Description[..500];
        report.Location.SRID = 4326;

        _dbContext.Hazards.Add(report);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _spatialCache.UpdateHazardCacheAsync(report);

        // Broadcast real-time alert to connected clients
        await _alertHub.Clients.All.SendAsync("HazardReported", new RouteAlert(
            report.Type, report.Description,
            report.Location.Y, report.Location.X, report.ReportedAt), cancellationToken);

        return CreatedAtAction(nameof(GetHazardById), new { id = report.Id }, report);
    }

    /// <summary>
    /// Retrieves a single hazard report by ID, falling back to OSM-backed synthetic hazards.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(HazardReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HazardReport>> GetHazardById(Guid id, CancellationToken cancellationToken = default)
    {
        var hazard = await _dbContext.Hazards.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (hazard is not null)
            return Ok(hazard);

        var merged = await _realHazardData.GetActiveHazardsAsync(null, null, null, null, null);
        var synthetic = merged.FirstOrDefault(h => h.Id == id);
        return synthetic is null ? NotFound() : Ok(synthetic);
    }

    /// <summary>
    /// Updates the status of an existing hazard report.
    /// </summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateHazardStatus(Guid id, [FromBody] HazardStatus status, CancellationToken cancellationToken = default)
    {
        var hazard = await _dbContext.Hazards.SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (hazard is null)
            return NotFound();

        hazard.Status = status;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _spatialCache.UpdateHazardCacheAsync(hazard);

        return NoContent();
    }
}
