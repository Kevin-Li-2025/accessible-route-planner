using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AccessCity.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class RoutingController : ControllerBase
{
    private readonly RoutingService _routing;
    private readonly RiskScoringService _risk;
    private readonly AppDbContext _dbContext;

    public RoutingController(RoutingService routing, RiskScoringService risk, AppDbContext dbContext)
    {
        _routing = routing;
        _risk = risk;
        _dbContext = dbContext;
    }

    [HttpPost("safe-path")]
    [ProducesResponseType(typeof(RouteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RouteResponse>> GetSafePath([FromBody] RouteRequest request, CancellationToken cancellationToken)
    {
        if (request.Start == null || request.End == null)
        {
            return BadRequest(new { error = "Both 'start' and 'end' coordinates are required." });
        }

        if (!IsValidCoordinate(request.Start) || !IsValidCoordinate(request.End))
        {
            return BadRequest(new { error = "Coordinates must be valid WGS-84 (lon/lat) values." });
        }

        if (request.SafetyWeight < 0 || request.SafetyWeight > 1)
        {
            return BadRequest(new { error = "'safetyWeight' must be between 0 and 1." });
        }

        var hazards = await LoadActiveHazardsAsync(cancellationToken);
        var result = await _routing.FindSafePathAsync(request, hazards, cancellationToken);
        if (result is null)
        {
            return NotFound(new
            {
                error = "No imported routing network covers the requested area. Import OSM data for this area first."
            });
        }

        return Ok(result);
    }

    [HttpGet("risk-score")]
    [ProducesResponseType(typeof(RiskScoreResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RiskScoreResponse>> GetRiskScore(
        [FromQuery] double lat,
        [FromQuery] double lng,
        [FromQuery] double radius = 500,
        CancellationToken cancellationToken = default)
    {
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
        {
            return BadRequest(new { error = "Invalid WGS-84 coordinates." });
        }

        if (radius <= 0 || radius > 5000)
        {
            return BadRequest(new { error = "Radius must be between 1 and 5000 metres." });
        }

        var hazards = await LoadActiveHazardsAsync(cancellationToken);
        var result = await _risk.EvaluateRiskAsync(lat, lng, radius, hazards);
        return Ok(result);
    }

    private async Task<List<HazardReport>> LoadActiveHazardsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Hazards
            .Where(h => h.Status == HazardStatus.Reported || h.Status == HazardStatus.UnderReview)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    private static bool IsValidCoordinate(NetTopologySuite.Geometries.Coordinate coordinate)
        => coordinate.X >= -180 && coordinate.X <= 180 && coordinate.Y >= -90 && coordinate.Y <= 90;
}
