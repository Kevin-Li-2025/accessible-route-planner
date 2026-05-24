using AccessCity.API.Common;
using AccessCity.API.Configuration;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Controllers;

/// <summary>
/// AI-assist endpoints that format text, review candidates, and explanations without influencing route decisions.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/ai-assist")]
public sealed class AiAssistController : ControllerBase
{
    private readonly IHazardReportService _hazards;
    private readonly IAiAssistService _aiAssist;
    private readonly IAccessibilityVerificationService _accessibilityVerifications;
    private readonly AiEnrichmentOptions _options;

    public AiAssistController(
        IHazardReportService hazards,
        IAiAssistService aiAssist,
        IAccessibilityVerificationService accessibilityVerifications,
        IOptions<AiEnrichmentOptions> options)
    {
        _hazards = hazards;
        _aiAssist = aiAssist;
        _accessibilityVerifications = accessibilityVerifications;
        _options = options.Value;
    }

    /// <summary>
    /// Normalizes a hazard report and returns duplicate and missing OSM attribute review candidates.
    /// </summary>
    [HttpGet("hazards/{id:guid}/enrichment")]
    [ProducesResponseType(typeof(HazardAiEnrichmentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<HazardAiEnrichmentResult>> GetHazardEnrichment(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError("AI assist is disabled."));
        }

        var hazard = await _hazards.GetByIdAsync(id, cancellationToken);
        if (hazard is null)
        {
            return NotFound(new ApiError("Hazard not found."));
        }

        var nearby = await GetNearbyHazardsAsync(hazard, cancellationToken);
        var enrichment = await _aiAssist.EnrichHazardAsync(hazard, nearby, cancellationToken);
        return Ok(enrichment);
    }

    /// <summary>
    /// Explains an already-computed route. This endpoint does not compute or alter routes.
    /// </summary>
    [HttpPost("route-explanation")]
    [ProducesResponseType(typeof(RouteExplanationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<RouteExplanationResponse>> ExplainRoute(
        [FromBody] RouteExplanationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError("AI assist is disabled."));
        }

        var explanation = await _aiAssist.ExplainRouteAsync(request, cancellationToken);
        return Ok(explanation);
    }

    /// <summary>
    /// Reviews a structured accessibility profile and returns human-verification suggestions.
    /// The result is advisory only and never updates routing decisions.
    /// </summary>
    [HttpGet("infrastructure/{assetId:long}/accessibility-review")]
    [ProducesResponseType(typeof(AccessibilityAiReviewResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessibilityAiReviewResult>> GetAccessibilityReview(
        long assetId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError("AI assist is disabled."));
        }

        var profile = await _accessibilityVerifications.GetProfileAsync(assetId, cancellationToken);
        if (profile is null)
        {
            return NotFound(new ApiError("Infrastructure asset not found."));
        }

        var review = await _aiAssist.ReviewAccessibilityProfileAsync(assetId, profile, cancellationToken);
        return Ok(review);
    }

    private async Task<IReadOnlyCollection<HazardReport>> GetNearbyHazardsAsync(
        HazardReport hazard,
        CancellationToken cancellationToken)
    {
        var radiusMetres = Math.Max(1, _options.DuplicateRadiusMetres);
        var latDelta = radiusMetres / 111_320d;
        var cosLat = Math.Cos(hazard.Location.Y * Math.PI / 180);
        var lngDelta = Math.Abs(cosLat) < 0.01 ? latDelta : radiusMetres / (111_320d * Math.Abs(cosLat));

        return await _hazards.GetHazardsAsync(
            hazard.Location.Y - latDelta,
            hazard.Location.X - lngDelta,
            hazard.Location.Y + latDelta,
            hazard.Location.X + lngDelta,
            null,
            cancellationToken);
    }
}
