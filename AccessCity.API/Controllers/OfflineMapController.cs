using AccessCity.API.Security;
using AccessCity.API.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AccessCity.API.Controllers
{
    /// <summary>
    /// Controller for managing offline map packs and tile pre-fetching.
    /// Handles the "bundling" logic for a specified geographic area.
    /// </summary>
    [Authorize]
    [ApiController]
    [ApiVersion("1.0")]
    [Route("api/v{version:apiVersion}/[controller]")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.HotRead)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.ShortRead)]
    public class OfflineMapController : ControllerBase
    {
        private readonly IOfflineMapBundleService _offlineMapBundles;

        public OfflineMapController(IOfflineMapBundleService offlineMapBundles)
        {
            _offlineMapBundles = offlineMapBundles;
        }

        /// <summary>
        /// Pre-fetches all hazard and infrastructure data for a specified bounding box.
        /// Primarily used to "warm up" the local cache on the device.
        /// </summary>
        [HttpGet("bundle")]
        public async Task<IActionResult> GetMapBundle(
            [FromQuery] double minLat,
            [FromQuery] double minLng,
            [FromQuery] double maxLat,
            [FromQuery] double maxLng,
            CancellationToken cancellationToken)
        {
            if (minLat < -90 || minLat > 90 || maxLat < -90 || maxLat > 90)
                return BadRequest(new { error = "Latitude values must be between -90 and 90." });
            if (minLng < -180 || minLng > 180 || maxLng < -180 || maxLng > 180)
                return BadRequest(new { error = "Longitude values must be between -180 and 180." });
            if (minLat > maxLat || minLng > maxLng)
                return BadRequest(new { error = "minLat must be <= maxLat and minLng must be <= maxLng." });

            var bundle = await _offlineMapBundles.GetBundleAsync(minLat, minLng, maxLat, maxLng, cancellationToken);
            return Ok(bundle);
        }
    }
}
