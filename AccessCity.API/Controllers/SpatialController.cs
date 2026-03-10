using Microsoft.AspNetCore.Mvc;
using AccessCity.API.Models;

namespace AccessCity.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SpatialController : ControllerBase
    {
        [HttpGet("poi")]
        public ActionResult<IEnumerable<PointOfInterest>> GetPointsOfInterest([FromQuery] double lat, [FromQuery] double lng, [FromQuery] double radius = 1000)
        {
            // Placeholder for Amrit's spatial logic
            return Ok(new List<PointOfInterest>());
        }

        [HttpGet("map-overlay")]
        public IActionResult GetMapOverlay([FromQuery] string layerName)
        {
            // Returns custom GeoJSON layers like lighting or sidewalk width
            return Ok(new { type = "FeatureCollection", features = new List<object>() });
        }
    }
}
