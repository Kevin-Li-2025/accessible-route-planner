using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AccessCity.API.Controllers
{
    [ApiController]
    [ApiVersion("1.0")]
    [Route("api/v{version:apiVersion}/[controller]")]
    public class DashboardController : ControllerBase
    {
        private readonly IRealHazardDataService _realHazardData;
        private readonly AppDbContext _db;

        public DashboardController(IRealHazardDataService realHazardData, AppDbContext db)
        {
            _realHazardData = realHazardData;
            _db = db;
        }

        /// <summary>
        /// Returns dashboard summary from real OSM hazard data: total hazards, pending, resolved.
        /// </summary>
        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary()
        {
            var hazards = await _realHazardData.GetActiveHazardsAsync();
            var totalHazards = hazards.Count;
            var pendingAlerts = hazards.Count(h =>
                h.Status == HazardStatus.Reported || h.Status == HazardStatus.UnderReview);

            var now = DateTime.UtcNow;
            var activeUsers = await _db.RefreshTokens.AsNoTracking()
                .Where(t => t.Revoked == null && t.Expires > now)
                .Select(t => t.UserId)
                .Distinct()
                .CountAsync();

            return Ok(new
            {
                TotalHazards = totalHazards,
                ActiveUsers = activeUsers,
                ActiveUsersDefinition = "Distinct accounts with at least one non-revoked, non-expired refresh token.",
                PendingAlerts = pendingAlerts,
                Resolved = hazards.Count(h => h.Status == HazardStatus.Resolved),
            });
        }

        /// <summary>
        /// Returns GeoJSON FeatureCollection of real hazard locations (OSM) for heat-map.
        /// </summary>
        [HttpGet("heat-map")]
        public async Task<IActionResult> GetHeatMap()
        {
            var hazards = await _realHazardData.GetActiveHazardsAsync();
            var features = new List<object>();

            foreach (var h in hazards)
            {
                if (h.Location == null) continue;

                var coords = new[] { h.Location.X, h.Location.Y };
                features.Add(new
                {
                    type = "Feature",
                    geometry = new
                    {
                        type = "Point",
                        coordinates = coords,
                    },
                    properties = new
                    {
                        id = h.Id,
                        type = h.Type,
                        status = h.Status.ToString(),
                        reportedAt = h.ReportedAt,
                    },
                });
            }

            return Ok(new
            {
                type = "FeatureCollection",
                features,
            });
        }

        /// <summary>
        /// Returns recent hazards from real OSM data as infrastructure feed (newest first by reportedAt).
        /// </summary>
        [HttpGet("infrastructure-feed")]
        public async Task<IActionResult> GetInfrastructureFeed([FromQuery] int limit = 20)
        {
            var hazards = await _realHazardData.GetActiveHazardsAsync();
            var feed = hazards
                .OrderByDescending(h => h.ReportedAt)
                .Take(Math.Clamp(limit, 1, 100))
                .Select(h => new
                {
                    id = h.Id,
                    type = h.Type,
                    description = h.Description,
                    status = h.Status.ToString(),
                    reportedAt = h.ReportedAt,
                    coordinates = h.Location != null
                        ? new[] { h.Location.X, h.Location.Y }
                        : (double[]?)null,
                })
                .ToList();

            return Ok(feed);
        }
    }
}
