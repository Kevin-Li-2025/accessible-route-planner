using Microsoft.AspNetCore.Mvc;

namespace AccessCity.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DashboardController : ControllerBase
    {
        [HttpGet("summary")]
        public IActionResult GetSummary()
        {
            return Ok(new { TotalHazards = 0, ActiveUsers = 0, PendingAlerts = 0 });
        }

        [HttpGet("heat-map")]
        public IActionResult GetHeatMap()
        {
            return Ok(new { type = "FeatureCollection", features = new List<object>() });
        }

        [HttpGet("infrastructure-feed")]
        public IActionResult GetInfrastructureFeed()
        {
            return Ok(new List<object>());
        }
    }
}
