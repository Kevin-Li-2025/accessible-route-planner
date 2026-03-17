using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AccessCity.API.Services;
using NetTopologySuite.Geometries;

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
        private readonly RoutingService _routingService;

        public OfflineMapController(ISpatialCacheService spatialCache, RoutingService routingService)
        {
            _spatialCache = spatialCache;
            _routingService = routingService;
        }

        /// <summary>
        /// Pre-fetches all hazard and infrastructure data for a specified bounding box.
        /// Primarily used to "warm up" the local cache on the device.
        /// </summary>
        [HttpGet("bundle")]
        public async Task<IActionResult> GetMapBundle([FromQuery] double minLat, [FromQuery] double minLng, [FromQuery] double maxLat, [FromQuery] double maxLng)
        {
            var bounds = new Envelope(minLng, maxLng, minLat, maxLat);
            var hazards = await _spatialCache.GetHazardsInBoundsAsync(bounds);

            return Ok(new
            {
                Area = new { minLat, minLng, maxLat, maxLng },
                Hazards = hazards,
                Timestamp = DateTime.UtcNow,
                Version = "1.0.0"
            });
        }
    }
}
