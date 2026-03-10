using Microsoft.AspNetCore.Mvc;
using AccessCity.API.Models;

namespace AccessCity.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HazardsController : ControllerBase
    {
        [HttpGet]
        public ActionResult<IEnumerable<HazardReport>> GetHazards([FromQuery] double minLat, [FromQuery] double minLng, [FromQuery] double maxLat, [FromQuery] double maxLng)
        {
            return Ok(new List<HazardReport>());
        }

        [HttpPost]
        public ActionResult<HazardReport> ReportHazard([FromBody] HazardReport report)
        {
            report.Id = Guid.NewGuid();
            report.ReportedAt = DateTime.UtcNow;
            report.Status = HazardStatus.Reported;
            return CreatedAtAction(nameof(GetHazardById), new { id = report.Id }, report);
        }

        [HttpGet("{id}")]
        public ActionResult<HazardReport> GetHazardById(Guid id)
        {
            return NotFound();
        }

        [HttpPatch("{id}")]
        public IActionResult UpdateHazardStatus(Guid id, [FromBody] HazardStatus status)
        {
            return NoContent();
        }
    }
}
