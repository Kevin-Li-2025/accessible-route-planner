using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AccessCity.API.Services;

namespace AccessCity.API.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/tiles")]
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
        public async Task<IActionResult> GetTile(int z, int x, int y)
        {
            string key = $"tile:{z}:{x}:{y}";
            if (!_bloomFilter.MightContain(key)) { }

            // Fetch/Generate Vector Tile
            var data = await _tileService.GetVectorTileAsync(z, x, y);

            if (data == null || data.Length == 0)
            {
                return NoContent();
            }

            return File(data, "application/x-protobuf");
        }
    }
}
