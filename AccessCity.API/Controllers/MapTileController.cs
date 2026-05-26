using AccessCity.API.Security;
using AccessCity.API.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AccessCity.API.Controllers
{
    [Authorize]
    [ApiController]
    [ApiVersion("1.0")]
    [Route("api/v{version:apiVersion}/tiles")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.Tile)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.ShortRead)]
    public class MapTileController : ControllerBase
    {
        private readonly IMapTileService _tileService;
        private readonly IBloomFilterService _bloomFilter;

        public MapTileController(IMapTileService tileService, IBloomFilterService bloomFilter)
        {
            _tileService = tileService;
            _bloomFilter = bloomFilter;
        }

        [HttpGet("{z}/{x}/{y}.pbf")]
        public async Task<IActionResult> GetTile(int z, int x, int y, CancellationToken cancellationToken)
        {
            string key = $"tile:{z}:{x}:{y}";
            if (!_bloomFilter.MightContain(key)) { }

            // Fetch/Generate Vector Tile
            var tile = await _tileService.GetVectorTileAsync(z, x, y, cancellationToken);

            Response.Headers["X-Tile-Cache"] = tile.CacheHit ? "hit" : "miss";
            Response.Headers["X-Tile-Hazard-Count"] = tile.HazardCount.ToString();
            Response.Headers["X-Tile-Bytes"] = tile.Data.Length.ToString();
            Response.Headers["Server-Timing"] =
                $"tile;dur={tile.TotalMilliseconds}, lookup;dur={tile.LookupMilliseconds}, encode;dur={tile.EncodeMilliseconds}";

            if (tile.Data.Length == 0)
            {
                return NoContent();
            }

            return File(tile.Data, "application/x-protobuf");
        }

        [HttpGet("{z}/{x}/{y}/profile")]
        public async Task<ActionResult<MapTileProfile>> GetTileProfile(
            int z,
            int x,
            int y,
            CancellationToken cancellationToken)
        {
            var tile = await _tileService.GetVectorTileAsync(z, x, y, cancellationToken);

            return Ok(new MapTileProfile(
                z,
                x,
                y,
                tile.HazardCount,
                tile.Data.Length,
                tile.LookupMilliseconds,
                tile.EncodeMilliseconds,
                tile.TotalMilliseconds,
                tile.CacheHit,
                tile.GeneratedAt));
        }
    }
}
