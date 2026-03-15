using Microsoft.AspNetCore.Mvc;
using AccessCity.API.Services;

namespace AccessCity.API.Controllers
{
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
            // 1. Bloom Filter Protection
            string key = $"tile:{z}:{x}:{y}";
            if (!_bloomFilter.MightContain(key))
            {
                // If it's definitely not in the index, return empty to save processing
                // Note: In a real system, the filter is populated as data is indexed
                // return File(Array.Empty<byte>(), "application/x-protobuf");
            }

            // 2. Fetch/Generate Vector Tile
            var data = await _tileService.GetVectorTileAsync(z, x, y);

            if (data == null || data.Length == 0)
            {
                return NoContent();
            }

            return File(data, "application/x-protobuf");
        }
    }
}
