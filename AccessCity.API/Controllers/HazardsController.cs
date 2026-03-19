using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AccessCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HazardsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ISpatialCacheService _spatialCache;

    public HazardsController(AppDbContext dbContext, ISpatialCacheService spatialCache)
    {
        _dbContext = dbContext;
        _spatialCache = spatialCache;
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
        if (report.Location is null)
        {
            return BadRequest(new { error = "Hazard location is required." });
        }

        report.Id = report.Id == Guid.Empty ? Guid.NewGuid() : report.Id;
        report.ReportedAt = DateTime.UtcNow;
        report.Status = HazardStatus.Reported;
        report.Source = string.IsNullOrWhiteSpace(report.Source) ? "user" : report.Source;
        report.Location.SRID = 4326;
        report.ReporterUserId ??= User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

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
        {
            return NotFound();
        }

        hazard.Status = status;
        await _dbContext.SaveChangesAsync();
        await _spatialCache.UpdateHazardCacheAsync(hazard);

        return NoContent();
    }
}
