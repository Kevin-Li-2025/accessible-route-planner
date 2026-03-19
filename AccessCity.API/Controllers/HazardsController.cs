using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Services;

namespace AccessCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HazardsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ISpatialCacheService _spatialCache;
    private readonly IRealHazardDataService _realHazardData;

    public HazardsController(AppDbContext dbContext, ISpatialCacheService spatialCache, IRealHazardDataService realHazardData)
    {
        _dbContext = dbContext;
        _spatialCache = spatialCache;
        _realHazardData = realHazardData;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<HazardReport>>> GetHazards(
        [FromQuery] double? minLat,
        [FromQuery] double? minLng,
        [FromQuery] double? maxLat,
        [FromQuery] double? maxLng)
    {
        if (new[] { minLat, minLng, maxLat, maxLng }.Any(value => value.HasValue) &&
            new[] { minLat, minLng, maxLat, maxLng }.Any(value => !value.HasValue))
        {
            return BadRequest(new { error = "Provide all bounding-box values or none of them." });
        }

        if (minLat.HasValue)
        {
            var hazardsInBounds = await _dbContext.Hazards
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM hazard_report
                    WHERE ST_Intersects(
                        geom,
                        ST_MakeEnvelope({minLng!.Value}, {minLat!.Value}, {maxLng!.Value}, {maxLat!.Value}, 4326))
                    ORDER BY reported_at DESC
                    """)
                .AsNoTracking()
                .ToListAsync();

            return Ok(hazardsInBounds);
        }

        var hazards = await _dbContext.Hazards
            .AsNoTracking()
            .OrderByDescending(hazard => hazard.ReportedAt)
            .ToListAsync();

        return Ok(hazards);
    }

    [HttpPost]
    public async Task<ActionResult<HazardReport>> ReportHazard([FromBody] HazardReport report)
    {
        if (report?.Location == null)
            return BadRequest(new { error = "Location is required." });
        
        if (string.IsNullOrWhiteSpace(report.Type))
            return BadRequest(new { error = "Type is required." });
        
        if (string.IsNullOrWhiteSpace(report.Description))
            return BadRequest(new { error = "Description is required." });

        var x = report.Location.X;
        var y = report.Location.Y;
        if (x < -180 || x > 180 || y < -90 || y > 90)
            return BadRequest(new { error = "Location must be valid WGS-84 coordinates (lon in [-180,180], lat in [-90,90])." });

        report.Id = report.Id == Guid.Empty ? Guid.NewGuid() : report.Id;
        report.ReportedAt = DateTime.UtcNow;
        report.Status = HazardStatus.Reported;
        report.Source = string.IsNullOrWhiteSpace(report.Source) ? "user" : report.Source;
        report.Location.SRID = 4326;
        report.ReporterUserId ??= User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        report.PhotoUrl ??= string.Empty;
        
        report.Type = report.Type.Trim();
        report.Description = report.Description.Trim();
        if (report.Type.Length > 50) report.Type = report.Type[..50];
        if (report.Description.Length > 500) report.Description = report.Description[..500];

        _dbContext.Hazards.Add(report);
        await _dbContext.SaveChangesAsync();
        await _spatialCache.UpdateHazardCacheAsync(report);

        return CreatedAtAction(nameof(GetHazardById), new { id = report.Id }, report);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<HazardReport>> GetHazardById(Guid id)
    {
        var hazard = await _dbContext.Hazards.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id);
        return hazard is null ? NotFound() : Ok(hazard);
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateHazardStatus(Guid id, [FromBody] HazardStatus status)
    {
        var hazard = await _dbContext.Hazards.SingleOrDefaultAsync(h => h.Id == id);
        if (hazard is null)
            return NotFound();

        hazard.Status = status;
        await _dbContext.SaveChangesAsync();
        await _spatialCache.UpdateHazardCacheAsync(hazard);

        return NoContent();
    }
}
    }
}
