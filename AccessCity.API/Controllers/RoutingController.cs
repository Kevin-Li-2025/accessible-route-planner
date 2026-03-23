using Asp.Versioning;
using AccessCity.API.Common;
using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AccessCity.API.Controllers;

/// <summary>
/// Provides safe-route computation, risk scoring, and async job management for route requests.
/// </summary>
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
    private readonly IRouteJobService _jobs;
    private readonly IRouteCoalescingService _coalescing;

    private static readonly TimeSpan SafePathTimeout = TimeSpan.FromSeconds(30);

    public RoutingController(
        RoutingService routing,
        RiskScoringService risk,
        PredictiveRiskModel aiRisk,
        AppDbContext dbContext,
        IRouteJobService jobs,
        IRouteCoalescingService coalescing)
    {
        _routing = routing;
        _risk = risk;
        _aiRisk = aiRisk;
        _dbContext = dbContext;
        _jobs = jobs;
        _coalescing = coalescing;
    }

    /// <summary>
    /// Computes a risk-aware safe path between two coordinates.
    /// Uses request coalescing to deduplicate identical concurrent requests.
    /// Returns 504 if computation exceeds 30 seconds.
    /// </summary>
    [HttpPost("safe-path")]
    [ProducesResponseType(typeof(RouteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<RouteResponse>> GetSafePath(
        [FromBody] RouteRequest request,
        CancellationToken cancellationToken)
    {
        var hazards = await LoadActiveHazardsAsync(cancellationToken);

        // Enforce a 30-second timeout to prevent indefinite blocking.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(SafePathTimeout);

        try
        {
            var result = await _coalescing.GetOrComputeAsync(
                request,
                async () => await _routing.FindSafePathAsync(request, hazards, timeoutCts.Token));

            if (result is null)
            {
                return NotFound(new ApiError(
                    "No route found.",
                    Detail: "The routing engine could not find a path. Ensure the chosen area is supported."));
            }

            return Ok(result);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new ApiError(
                "Route computation timed out.",
                Detail: $"The A* pathfinding exceeded the {SafePathTimeout.TotalSeconds}s limit. Consider using the async job endpoint: POST /routing/safe-path/async."));
        }
    }

    /// <summary>
    /// Submits a safe-path computation as an async job. Returns a job ID immediately (202 Accepted).
    /// Use GET /routing/jobs/{jobId} to poll for the result.
    /// This prevents HTTP connection pool exhaustion under high concurrency.
    /// </summary>
    [HttpPost("safe-path/async")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitSafePathJob(
        [FromBody] RouteRequest request,
        CancellationToken cancellationToken)
    {
        var hazards = await LoadActiveHazardsAsync(cancellationToken);
        var jobId = _jobs.Submit(request, hazards);

        return Accepted(new { jobId, status = "pending", pollUrl = $"/api/v1/routing/jobs/{jobId}" });
    }

    /// <summary>
    /// Polls the status of an async route computation job.
    /// </summary>
    [HttpGet("jobs/{jobId}")]
    [ProducesResponseType(typeof(RouteJobResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public ActionResult<RouteJobResult> GetJobStatus(string jobId)
    {
        var result = _jobs.GetResult(jobId);
        if (result is null)
            return NotFound(new ApiError("Job not found.", Detail: "The job ID may have expired (TTL: 5 minutes)."));

        return Ok(result);
    }

    /// <summary>
    /// Multi-objective routing: same recommendation as <c>safe-path</c> when OSRM is used, plus up to three labelled
    /// alternatives (shortest distance, lowest composite risk, fastest time) when the router returns multiple geometries.
    /// </summary>
    [HttpPost("safe-path/options")]
    [ProducesResponseType(typeof(SafePathOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SafePathOptionsResponse>> GetSafePathOptions(
        [FromBody] RouteRequest request,
        CancellationToken cancellationToken = default)
    {
        var hazards = await LoadActiveHazardsAsync(cancellationToken);
        var result = await _routing.FindSafePathWithVariantsAsync(request, hazards, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Evaluates the composite risk score for a given coordinate within a specified radius.
    /// </summary>
    [HttpGet("risk-score")]
    [ProducesResponseType(typeof(RiskScoreResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RiskScoreResponse>> GetRiskScore(
        [FromQuery] RiskScoreRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var hazards = await LoadActiveHazardsAsync(cancellationToken);
        var result = await _risk.EvaluateRiskAsync(request.Lat, request.Lng, request.Radius, hazards);
        return Ok(result);
    }

    /// <summary>
    /// Returns a predictive risk score using the AI/ML logistic model with time-of-day and weather factors.
    /// </summary>
    [HttpGet("ai-risk-score")]
    [ProducesResponseType(typeof(PredictiveRiskResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
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
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
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
