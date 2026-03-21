using Asp.Versioning;
using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AccessCity.API.Controllers;

[AllowAnonymous]
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public class RoutingController : ControllerBase
{
    private readonly RoutingService _routing;
    private readonly RiskScoringService _risk;
    private readonly PredictiveRiskModel _aiRisk;
    private readonly AppDbContext _dbContext;

    public RoutingController(RoutingService routing, RiskScoringService risk, PredictiveRiskModel aiRisk, AppDbContext dbContext)
    {
        _routing = routing;
        _risk = risk;
        _aiRisk = aiRisk;
        _dbContext = dbContext;
    }

    [HttpPost("safe-path")]
    [ProducesResponseType(typeof(RouteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RouteResponse>> GetSafePath([FromBody] RouteRequest request, CancellationToken cancellationToken)
    {
        // FluentValidation automatically returns 400 if RouteRequest is invalid
        var hazards = await LoadActiveHazardsAsync(cancellationToken);
        var result = await _routing.FindSafePathAsync(request, hazards);

        if (result is null)
        {
            return NotFound(new
            {
                error = "No route found.",
                hint = "The routing engine could not find a path. If you are using real-world routing, ensure OSRM is reachable. Otherwise, check if the chosen area is supported."
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Multi-objective routing: same recommendation as <c>safe-path</c> when OSRM is used, plus up to three labelled
    /// alternatives (shortest distance, lowest composite risk, fastest time) when the router returns multiple geometries.
    /// </summary>
    [HttpPost("safe-path/options")]
    [ProducesResponseType(typeof(SafePathOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SafePathOptionsResponse>> GetSafePathOptions(
        [FromBody] RouteRequest request,
        CancellationToken cancellationToken = default)
    {
        var hazards = await LoadActiveHazardsAsync(cancellationToken);
        var result = await _routing.FindSafePathWithVariantsAsync(request, hazards);
        return Ok(result);
    }

    [HttpGet("risk-score")]
    [ProducesResponseType(typeof(RiskScoreResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RiskScoreResponse>> GetRiskScore(
        [FromQuery] RiskScoreRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var hazards = await LoadActiveHazardsAsync(cancellationToken);
        var result = await _risk.EvaluateRiskAsync(request.Lat, request.Lng, request.Radius, hazards);
        return Ok(result);
    }

    [HttpGet("ai-risk-score")]
    [ProducesResponseType(typeof(PredictiveRiskResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PredictiveRiskResult>> GetAiRiskScore(
        [FromQuery] RiskScoreRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var hazards = await LoadActiveHazardsAsync(cancellationToken);
        var result = await _aiRisk.EvaluateSegmentRiskAsync(request.Lat, request.Lng, hazards, request.Radius);
        return Ok(result);
    }

    /// <summary>
    /// Alternative blend: full <see cref="RiskScoringService.EvaluateRiskAsync"/> plus time-of-day and live weather weights.
    /// Prefer <c>ai-risk-score</c> for the logistic multi-factor model used in routing.
    /// </summary>
    [HttpGet("hazard-blend-risk")]
    [ProducesResponseType(typeof(PredictiveRiskResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PredictiveRiskResult>> GetHazardBlendRisk(
        [FromQuery] RiskScoreRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var hazards = await LoadActiveHazardsAsync(cancellationToken);
        var result = await _risk.PredictRiskAsync(request.Lat, request.Lng, request.Radius, hazards);
        return Ok(result);
    }

    private async Task<List<HazardReport>> LoadActiveHazardsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Hazards
            .Where(h => h.Status == HazardStatus.Reported || h.Status == HazardStatus.UnderReview)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
}
