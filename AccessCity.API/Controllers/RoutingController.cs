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

        // ── POST /api/routing/safe-path ─────────────────────────────

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
            // ── Input validation ──
            if (request.Start == null || request.End == null)
                return BadRequest(new { error = "Both 'start' and 'end' coordinates are required." });

            if (!IsValidCoordinate(request.Start) || !IsValidCoordinate(request.End))
                return BadRequest(new { error = "Coordinates must be valid WGS-84 (lon/lat) values." });

            if (request.SafetyWeight < 0 || request.SafetyWeight > 1)
                return BadRequest(new { error = "'safetyWeight' must be between 0 and 1." });

            // ── Fetch active hazards ──
            // In production, query the database.  For the PoC, use the in-memory store.
            var hazards = GetActiveHazards();

            // ── Route ──
            var result = _routing.FindSafePath(request, hazards);

            return Ok(result);
        }

        // ── GET /api/routing/risk-score ─────────────────────────────

        /// <summary>
        /// Compute a predictive risk score for a given location.
        /// 
        /// Returns a composite score (0 = perfectly safe, 1 = maximum risk)
        /// with a detailed breakdown of proximity, density, and infrastructure factors.
        /// </summary>
        [HttpGet("risk-score")]
        [ProducesResponseType(typeof(RiskScoreResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<RiskScoreResponse> GetRiskScore(
            [FromQuery] double lat,
            [FromQuery] double lng,
            [FromQuery] double radius = 500)
        {
            if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
                return BadRequest(new { error = "Invalid WGS-84 coordinates." });

            if (radius <= 0 || radius > 5000)
                return BadRequest(new { error = "Radius must be between 1 and 5000 metres." });

            var hazards = GetActiveHazards();
            var result = _risk.EvaluateRisk(lat, lng, radius, hazards);

            return Ok(result);
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static bool IsValidCoordinate(NetTopologySuite.Geometries.Coordinate c)
            => c.X >= -180 && c.X <= 180 && c.Y >= -90 && c.Y <= 90;

        /// <summary>
        /// In-memory hazard data source for the PoC.
        /// Provides realistic sample data around Birmingham, UK.
        /// In production this is replaced by a PostGIS query.
        /// </summary>
        private static List<HazardReport> GetActiveHazards()
        {
            return new List<HazardReport>
            {
                // ── Birmingham city-centre hazards ──
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0001-4000-8000-000000000001"),
                    Location = new NetTopologySuite.Geometries.Point(-1.9003, 52.4814),
                    Type = "pothole",
                    Description = "Large pothole on New Street near the station.",
                    ReportedAt = DateTime.UtcNow.AddDays(-3),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0002-4000-8000-000000000002"),
                    Location = new NetTopologySuite.Geometries.Point(-1.8975, 52.4792),
                    Type = "poor_lighting",
                    Description = "Poorly lit underpass beneath the ring road.",
                    ReportedAt = DateTime.UtcNow.AddDays(-7),
                    Status = HazardStatus.UnderReview
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0003-4000-8000-000000000003"),
                    Location = new NetTopologySuite.Geometries.Point(-1.9031, 52.4835),
                    Type = "construction",
                    Description = "Active construction near Paradise Circus.",
                    ReportedAt = DateTime.UtcNow.AddDays(-1),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0004-4000-8000-000000000004"),
                    Location = new NetTopologySuite.Geometries.Point(-1.8950, 52.4780),
                    Type = "missing_curb_ramp",
                    Description = "Missing kerb ramp at the junction of Digbeth High Street.",
                    ReportedAt = DateTime.UtcNow.AddDays(-14),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0005-4000-8000-000000000005"),
                    Location = new NetTopologySuite.Geometries.Point(-1.9051, 52.4862),
                    Type = "broken_pavement",
                    Description = "Broken pavement on Broad Street near Five Ways.",
                    ReportedAt = DateTime.UtcNow.AddDays(-5),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0006-4000-8000-000000000006"),
                    Location = new NetTopologySuite.Geometries.Point(-1.8915, 52.4833),
                    Type = "obstruction",
                    Description = "Temporary bollards blocking footpath on Corporation Street.",
                    ReportedAt = DateTime.UtcNow.AddDays(-2),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0007-4000-8000-000000000007"),
                    Location = new NetTopologySuite.Geometries.Point(-1.8990, 52.4755),
                    Type = "missing_crossing",
                    Description = "No pedestrian crossing on busy A38 Bristol Road section.",
                    ReportedAt = DateTime.UtcNow.AddDays(-10),
                    Status = HazardStatus.UnderReview
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0008-4000-8000-000000000008"),
                    Location = new NetTopologySuite.Geometries.Point(-1.9080, 52.4510),
                    Type = "steep_gradient",
                    Description = "Steep hill on Bristol Road near University of Birmingham.",
                    ReportedAt = DateTime.UtcNow.AddDays(-20),
                    Status = HazardStatus.Reported
                },
                // ── University of Birmingham campus area ──
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0009-4000-8000-000000000009"),
                    Location = new NetTopologySuite.Geometries.Point(-1.9300, 52.4510),
                    Type = "uneven_surface",
                    Description = "Uneven cobbled path near the clock tower, UoB campus.",
                    ReportedAt = DateTime.UtcNow.AddDays(-8),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-000a-4000-8000-000000000010"),
                    Location = new NetTopologySuite.Geometries.Point(-1.9275, 52.4525),
                    Type = "missing_tactile",
                    Description = "No tactile paving at the south entrance to campus.",
                    ReportedAt = DateTime.UtcNow.AddDays(-12),
                    Status = HazardStatus.Reported
                },
            };
        }
    }
}
