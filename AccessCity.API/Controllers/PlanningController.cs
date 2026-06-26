using Asp.Versioning;
using AccessCity.API.Common;
using AccessCity.API.Models;
using AccessCity.API.Security;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AccessCity.API.Controllers;

/// <summary>
/// Read-only planning decision support for accessibility data quality and repair prioritization.
/// </summary>
[AllowAnonymous]
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public sealed class PlanningController : ControllerBase
{
    private readonly IAccessibilityPlanningService _planning;

    public PlanningController(IAccessibilityPlanningService planning)
    {
        _planning = planning;
    }

    /// <summary>
    /// Summarizes accessibility metadata quality in a bounding box and ranks field-verification candidates.
    /// </summary>
    [HttpPost("accessibility-quality")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.HotRead)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.ShortRead)]
    [ProducesResponseType(typeof(AccessibilityDataQualitySummary), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AccessibilityDataQualitySummary>> AnalyzeAccessibilityQuality(
        [FromBody] AccessibilityPlanningRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _planning.AnalyzeAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiError("Invalid planning request.", Detail: ex.Message));
        }
    }
}
