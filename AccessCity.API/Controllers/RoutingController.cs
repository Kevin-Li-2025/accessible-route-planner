using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AccessCity.API.Models;
using AccessCity.API.Services;

namespace AccessCity.API.Controllers
{
    /// <summary>
    /// Safety-Aware Routing API (F-1).
    /// 
    /// Provides two core capabilities:
    ///   POST /api/routing/safe-path   — Compute an accessible, safety-optimised route
    ///   GET  /api/routing/risk-score  — Predictive risk scoring at a geographic point
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class RoutingController : ControllerBase
    {
        private readonly RoutingService     _routing;
        private readonly RiskScoringService _risk;

        public RoutingController(RoutingService routing, RiskScoringService risk)
        {
            _routing = routing;
            _risk    = risk;
        }

        /// <summary>
        /// Compute an accessible, safety-aware route between two points.
        /// 
        /// The response includes:
        ///   • A GeoJSON LineString path
        ///   • Total distance (m) and estimated walking time (s)
        ///   • A composite safety score (0–1)
        ///   • Turn-by-turn steps with per-segment safety
        ///   • Contextual warnings (stairs, construction, poor lighting, etc.)
        /// </summary>
        [HttpPost("safe-path")]
        [ProducesResponseType(typeof(RouteResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<RouteResponse> GetSafePath([FromBody] RouteRequest request)
        {
            if (request.Start == null || request.End == null)
                return BadRequest(new { error = "Both 'start' and 'end' coordinates are required." });

            if (!IsValidCoordinate(request.Start) || !IsValidCoordinate(request.End))
                return BadRequest(new { error = "Coordinates must be valid WGS-84 (lon/lat) values." });

            if (request.SafetyWeight < 0 || request.SafetyWeight > 1)
                return BadRequest(new { error = "'safetyWeight' must be between 0 and 1." });

            var hazards = GetActiveHazards();
            var result = _routing.FindSafePath(request, hazards);

            return Ok(result);
        }

        /// <summary>
        /// Compute a predictive risk score for a given location.
        /// 
        /// Returns a composite score (0 = perfectly safe, 1 = maximum risk)
        /// with a detailed breakdown: hazard proximity, density, infrastructure, and UK Police street crime (cached 24h).
        /// </summary>
        [HttpGet("risk-score")]
        [ProducesResponseType(typeof(RiskScoreResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<RiskScoreResponse>> GetRiskScore(
            [FromQuery] double lat,
            [FromQuery] double lng,
            [FromQuery] double radius = 500)
        {
            if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
                return BadRequest(new { error = "Invalid WGS-84 coordinates." });

            if (radius <= 0 || radius > 5000)
                return BadRequest(new { error = "Radius must be between 1 and 5000 metres." });

            var hazards = GetActiveHazards();
            var result = await _risk.EvaluateRiskAsync(lat, lng, radius, hazards);

            return Ok(result);
        }

        private static bool IsValidCoordinate(NetTopologySuite.Geometries.Coordinate c)
            => c.X >= -180 && c.X <= 180 && c.Y >= -90 && c.Y <= 90;

        /// <summary>
        /// In-memory hazard data for the prototype.
        /// </summary>
        private static List<HazardReport> GetActiveHazards()
        {
            return AccessCity.API.Data.StaticHazardData.GetActiveHazards();
        }
    }
}
