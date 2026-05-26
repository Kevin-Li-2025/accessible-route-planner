using AccessCity.API.Common;
using AccessCity.API.Models;
using AccessCity.API.Security;
using AccessCity.API.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace AccessCity.API.Controllers;

/// <summary>
/// Spatial queries: points of interest (PostGIS proximity) and themed map overlays.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[EnableRateLimiting(AccessCityRateLimitPolicies.HotRead)]
[RequestTimeout(AccessCityRequestTimeoutPolicies.ShortRead)]
public class SpatialController : ControllerBase
{
    private readonly ISpatialQueryService _spatialQueries;
    private readonly IAccessibilityVerificationService _accessibilityVerifications;

    public SpatialController(
        ISpatialQueryService spatialQueries,
        IAccessibilityVerificationService accessibilityVerifications)
    {
        _spatialQueries = spatialQueries;
        _accessibilityVerifications = accessibilityVerifications;
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

        var points = await _spatialQueries.GetPointsOfInterestAsync(lat, lng, radius, cancellationToken);
        return Ok(points);
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
        var overlay = await _spatialQueries.GetMapOverlayAsync(layerName, cancellationToken);
        return overlay is null
            ? BadRequest(new ApiError("Supported layers: hazards, infrastructure."))
            : Ok(overlay);
    }

    /// <summary>
    /// Returns the structured accessibility profile for an infrastructure asset.
    /// </summary>
    [HttpGet("infrastructure/{assetId:long}/accessibility-profile")]
    [ProducesResponseType(typeof(InfrastructureAccessibilityProfile), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InfrastructureAccessibilityProfile>> GetAccessibilityProfile(
        long assetId,
        CancellationToken cancellationToken = default)
    {
        var profile = await _accessibilityVerifications.GetProfileAsync(assetId, cancellationToken);
        return profile is null ? NotFound(new ApiError("Infrastructure asset not found.")) : Ok(profile);
    }

    /// <summary>
    /// Submits field-verified accessibility data for review. The submission is auditable and
    /// does not alter routing graph edge costs.
    /// </summary>
    [Authorize]
    [HttpPost("infrastructure/{assetId:long}/accessibility-verifications")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.Write)]
    [ProducesResponseType(typeof(AccessibilityVerificationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AccessibilityVerificationResponse>> SubmitAccessibilityVerification(
        long assetId,
        [FromBody] AccessibilityVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _accessibilityVerifications.SubmitAsync(
            assetId,
            request,
            ResolveUserId(),
            cancellationToken);

        return response is null
            ? NotFound(new ApiError("Infrastructure asset not found."))
            : Accepted(response);
    }

    /// <summary>
    /// Lists recent accessibility verification submissions for an infrastructure asset.
    /// </summary>
    [Authorize]
    [HttpGet("infrastructure/{assetId:long}/accessibility-verifications")]
    [ProducesResponseType(typeof(IEnumerable<AccessibilityVerificationResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<AccessibilityVerificationResponse>>> GetAccessibilityVerifications(
        long assetId,
        CancellationToken cancellationToken = default)
    {
        var submissions = await _accessibilityVerifications.ListAsync(assetId, cancellationToken);
        return Ok(submissions);
    }

    private string ResolveUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.NameId)
            ?? User.Identity?.Name
            ?? "unknown";
    }
}
