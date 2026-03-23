using System.Text.Json;
using AccessCity.API.Common;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Controllers;

/// <summary>
/// Spatial queries: points of interest (PostGIS proximity) and themed map overlays.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public class SpatialController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public SpatialController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Returns points of interest within a radius of the given coordinate, ordered by proximity.
    /// </summary>
    [HttpGet("poi")]
    [ProducesResponseType(typeof(IEnumerable<PointOfInterest>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<PointOfInterest>>> GetPointsOfInterest(
        [FromQuery] double lat,
        [FromQuery] double lng,
        [FromQuery] double radius = 1000,
        CancellationToken cancellationToken = default)
    {
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
        {
            return BadRequest(new ApiError("Invalid coordinates."));
        }

        if (radius <= 0 || radius > 10000)
        {
            return BadRequest(new ApiError("Radius must be between 1 and 10000 metres."));
        }

        var assets = await _dbContext.InfrastructureAssets
            .FromSqlInterpolated($"""
                SELECT *
                FROM infrastructure_assets
                WHERE ST_DWithin(
                    "Geometry"::geography,
                    ST_SetSRID(ST_MakePoint({lng}, {lat}), 4326)::geography,
                    {radius})
                ORDER BY ST_Distance(
                    "Geometry"::geography,
                    ST_SetSRID(ST_MakePoint({lng}, {lat}), 4326)::geography)
                LIMIT 100
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return Ok(assets.Select(asset =>
        {
            var centroid = asset.Geometry is Point point ? point : asset.Geometry.Centroid;
            return new PointOfInterest
            {
                Id = Guid.NewGuid(),
                Name = asset.Name ?? asset.AssetType,
                Category = asset.AssetType,
                Location = new Point(centroid.X, centroid.Y) { SRID = 4326 },
                AccessibilityTags = ParseTags(asset.AccessibilityInfo)
            };
        }).ToList());
    }

    /// <summary>
    /// Returns a themed map overlay (hazards or infrastructure) as a list of spatial features.
    /// </summary>
    [HttpGet("map-overlay")]
    [ProducesResponseType(typeof(MapOverlayResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetMapOverlay(
        [FromQuery] string layerName,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(layerName, "hazards", StringComparison.OrdinalIgnoreCase))
        {
            var hazards = await _dbContext.Hazards
                .AsNoTracking()
                .OrderByDescending(hazard => hazard.ReportedAt)
                .Take(250)
                .ToListAsync(cancellationToken);

            return Ok(new MapOverlayResponse
            {
                Layer = "hazards",
                Features = hazards.Select(hazard => new MapOverlayFeature
                {
                    Geometry = hazard.Location,
                    Properties = new
                    {
                        hazard.Id,
                        hazard.Type,
                        Status = hazard.Status.ToString(),
                        hazard.Description,
                        hazard.ReportedAt
                    }
                }).ToList()
            });
        }

        if (string.Equals(layerName, "infrastructure", StringComparison.OrdinalIgnoreCase))
        {
            var assets = await _dbContext.InfrastructureAssets
                .AsNoTracking()
                .OrderByDescending(asset => asset.UpdatedAt)
                .Take(250)
                .ToListAsync(cancellationToken);

            return Ok(new MapOverlayResponse
            {
                Layer = "infrastructure",
                Features = assets.Select(asset => new MapOverlayFeature
                {
                    Geometry = asset.Geometry,
                    Properties = new
                    {
                        asset.Id,
                        asset.AssetType,
                        asset.Name,
                        asset.Status,
                        AccessibilityTags = ParseTags(asset.AccessibilityInfo)
                    }
                }).ToList()
            });
        }

        return BadRequest(new ApiError("Supported layers: hazards, infrastructure."));
    }

    private static Dictionary<string, string> ParseTags(JsonDocument json)
    {
        return json.RootElement.ValueKind == JsonValueKind.Object
            ? json.RootElement.EnumerateObject().ToDictionary(prop => prop.Name, prop => prop.Value.ToString())
            : new Dictionary<string, string>();
    }
}
