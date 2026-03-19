using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AccessCity.API.Data;
using AccessCity.API.Services;
using NetTopologySuite.Geometries;
using Microsoft.EntityFrameworkCore;

namespace AccessCity.API.Controllers
{
    /// <summary>
    /// Controller for managing offline map packs and tile pre-fetching.
    /// Handles the "bundling" logic for a specified geographic area.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class OfflineMapController : ControllerBase
    {
        private readonly ISpatialCacheService _spatialCache;
        private readonly AppDbContext _dbContext;
        private readonly IRealHazardDataService _realHazardData;

        public OfflineMapController(ISpatialCacheService spatialCache, AppDbContext dbContext, IRealHazardDataService realHazardData)
        {
            _spatialCache = spatialCache;
            _dbContext = dbContext;
            _realHazardData = realHazardData;
        }

        /// <summary>
        /// Pre-fetches all hazard and infrastructure data for a specified bounding box.
        /// Primarily used to "warm up" the local cache on the device.
        /// </summary>
        [HttpGet("bundle")]
        public async Task<IActionResult> GetMapBundle([FromQuery] double minLat, [FromQuery] double minLng, [FromQuery] double maxLat, [FromQuery] double maxLng)
        {
            if (minLat < -90 || minLat > 90 || maxLat < -90 || maxLat > 90)
                return BadRequest(new { error = "Latitude values must be between -90 and 90." });
            if (minLng < -180 || minLng > 180 || maxLng < -180 || maxLng > 180)
                return BadRequest(new { error = "Longitude values must be between -180 and 180." });
            if (minLat > maxLat || minLng > maxLng)
                return BadRequest(new { error = "minLat must be <= maxLat and minLng must be <= maxLng." });

            var bounds = new Envelope(minLng, maxLng, minLat, maxLat);
            var hazards = await _spatialCache.GetHazardsInBoundsAsync(bounds);
            
            // Optionally supplement with real-time data if needed, or keep them separate.
            // For now, we'll return the cached hazards which should include real-time ones if the cache is updated.
            
            var infrastructure = await _dbContext.InfrastructureAssets
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM infrastructure_assets
                    WHERE ST_Intersects(
                        "Geometry",
                        ST_MakeEnvelope({minLng}, {minLat}, {maxLng}, {maxLat}, 4326))
                    """)
                .AsNoTracking()
                .ToListAsync();

            return Ok(new
            {
                Area = new { minLat, minLng, maxLat, maxLng },
                Hazards = hazards,
                Infrastructure = infrastructure,
                Timestamp = DateTime.UtcNow,
                Version = "1.0.0"
            });
        }
    }
}
