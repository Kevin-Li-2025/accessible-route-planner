using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Json;

namespace AccessCity.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GeocodingController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public GeocodingController(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return BadRequest("Query is required");

            var client = _httpClientFactory.CreateClient("Nominatim");
            var url = $"search?q={Uri.EscapeDataString(query)}&format=json&limit=5";

            try
            {
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    return StatusCode((int)response.StatusCode, new { error = $"Nominatim error: {error}" });
                }
                var results = await response.Content.ReadFromJsonAsync<List<NominatimResult>>();
                return Ok(results);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"Geocoding error: {ex.Message}" });
            }
        }

        [HttpGet("reverse")]
        public async Task<IActionResult> Reverse([FromQuery] double lat, [FromQuery] double lon)
        {
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
                return BadRequest(new { error = "Invalid WGS-84 coordinates (lat in [-90,90], lon in [-180,180])." });

            var client = _httpClientFactory.CreateClient("Nominatim");
            var url = $"reverse?lat={lat}&lon={lon}&format=json";

            try
            {
                var result = await client.GetFromJsonAsync<NominatimResult>(url);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"Reverse geocoding error: {ex.Message}" });
            }
        }
    }

    public class NominatimResult
    {
        public long place_id { get; set; }
        public string licence { get; set; } = string.Empty;
        public string osm_type { get; set; } = string.Empty;
        public long osm_id { get; set; }
        public List<string> boundingbox { get; set; } = new();
        public string lat { get; set; } = string.Empty;
        public string lon { get; set; } = string.Empty;
        public string display_name { get; set; } = string.Empty;
        public string @class { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public double importance { get; set; }
    }
}
