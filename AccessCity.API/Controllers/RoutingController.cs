using System.Diagnostics;
using Asp.Versioning;
using AccessCity.API.Common;
using AccessCity.API.Configuration;
using AccessCity.API.Exceptions;
using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
    private readonly IRoutingService _routing;
    private readonly IRiskScoringService _risk;
    private readonly IPredictiveRiskModel _aiRisk;
    private readonly IHazardQueryService _hazardQueries;
    private readonly IRouteJobService _jobs;
    private readonly IRouteCoalescingService _coalescing;
    private readonly IRouteComputationLimiter _routeLimiter;
    private readonly IRouteCacheService _routeCache;
    private readonly IRiskScoreCacheService _riskScoreCache;
    private readonly AccessCityMetrics _metrics;
    private readonly RoutingOptions _routingOptions;

    public RoutingController(
        IRoutingService routing,
        IRiskScoringService risk,
        IPredictiveRiskModel aiRisk,
        IHazardQueryService hazardQueries,
        IRouteJobService jobs,
        IRouteCoalescingService coalescing,
        IRouteComputationLimiter routeLimiter,
        IRouteCacheService routeCache,
        IRiskScoreCacheService riskScoreCache,
        AccessCityMetrics metrics,
        IOptions<RoutingOptions> routingOptions)
    {
        _routing = routing;
        _risk = risk;
        _aiRisk = aiRisk;
        _hazardQueries = hazardQueries;
        _jobs = jobs;
        _coalescing = coalescing;
        _routeLimiter = routeLimiter;
        _routeCache = routeCache;
        _riskScoreCache = riskScoreCache;
        _metrics = metrics;
        _routingOptions = routingOptions.Value;
    }

    /// <summary>
    /// Computes a risk-aware safe path between two coordinates.
    /// Uses request coalescing to deduplicate identical concurrent requests.
    /// Returns 504 if computation exceeds 30 seconds.
    /// </summary>
    [HttpPost("safe-path")]
    [ProducesResponseType(typeof(RouteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<RouteResponse>> GetSafePath(
        [FromBody] RouteRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        // Enforce a short synchronous timeout; heavier work belongs on the async job path.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var safePathTimeout = TimeSpan.FromSeconds(Math.Max(1, _routingOptions.SyncSafePathTimeoutSeconds));
        timeoutCts.CancelAfter(safePathTimeout);

        try
        {
            if (_routingOptions.AsyncFirstForCacheMiss)
            {
                var cacheKey = _routeCache.BuildKey(
                    request.Start.Y,
                    request.Start.X,
                    request.End.Y,
                    request.End.X,
                    request.Profile ?? "standard",
                    request.SafetyWeight);
                var cached = await _routeCache.TryGetAsync(cacheKey);
                if (cached is not null)
                {
                    RecordSafePath(stopwatch, "cache_hit");
                    return Ok(cached);
                }

                var jobId = await _jobs.SubmitAsync(request, cancellationToken: cancellationToken);
                RecordSafePath(stopwatch, "async_accepted");
                return Accepted(new { jobId, status = "pending", pollUrl = $"/api/v1/routing/jobs/{jobId}" });
            }

            var queueTimeout = TimeSpan.FromSeconds(Math.Max(1, _routingOptions.ComputationQueueTimeoutSeconds));
            var result = await _coalescing.GetOrComputeAsync(
                request,
                async () =>
                {
                    await using var lease = await _routeLimiter.TryAcquireAsync(queueTimeout, timeoutCts.Token)
                        ?? throw new RouteCapacityExceededException();

                    var hazards = await _hazardQueries.LoadHazardsForRouteAsync(request, timeoutCts.Token);
                    return await _routing.FindSafePathAsync(request, hazards, timeoutCts.Token);
                });

            if (result is null)
            {
                RecordSafePath(stopwatch, "not_found");
                return NotFound(new ApiError(
                    "No route found.",
                    Detail: "The routing engine could not find a path. Ensure the chosen area is supported."));
            }

            RecordSafePath(stopwatch, "success");
            return Ok(result);
        }
        catch (RouteCapacityExceededException)
        {
            RecordSafePath(stopwatch, "capacity_saturated");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError(
                "Route computation capacity is saturated.",
                Detail: "Retry shortly or use the async job endpoint: POST /routing/safe-path/async."));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            RecordSafePath(stopwatch, "timeout");
            return StatusCode(StatusCodes.Status504GatewayTimeout, new ApiError(
                "Route computation timed out.",
                Detail: $"The route computation exceeded the {safePathTimeout.TotalSeconds}s limit. Consider using the async job endpoint: POST /routing/safe-path/async."));
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
        var jobId = await _jobs.SubmitAsync(request, cancellationToken: cancellationToken);

        return Accepted(new { jobId, status = "pending", pollUrl = $"/api/v1/routing/jobs/{jobId}" });
    }

    /// <summary>
    /// Polls the status of an async route computation job.
    /// </summary>
    [HttpGet("jobs/{jobId}")]
    [ProducesResponseType(typeof(RouteJobResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RouteJobResult>> GetJobStatus(string jobId, CancellationToken cancellationToken)
    {
        var result = await _jobs.GetResultAsync(jobId, cancellationToken);
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
        var stopwatch = Stopwatch.StartNew();
        var queueTimeout = TimeSpan.FromSeconds(Math.Max(1, _routingOptions.ComputationQueueTimeoutSeconds));
        await using var lease = await _routeLimiter.TryAcquireAsync(queueTimeout, cancellationToken);
        if (lease is null)
        {
            RecordSafePathOptions(stopwatch, "capacity_saturated");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError(
                "Route computation capacity is saturated.",
                Detail: "Retry shortly or use the async job endpoint for the primary route."));
        }

        var hazards = await _hazardQueries.LoadHazardsForRouteAsync(request, cancellationToken);
        var result = await _routing.FindSafePathWithVariantsAsync(request, hazards, cancellationToken);
        RecordSafePathOptions(stopwatch, "success");
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
        var cappedRadius = Math.Min(
            Math.Max(0, request.Radius),
            Math.Max(1, _routingOptions.MaxRiskQueryRadiusMetres));
        var cacheKey = _riskScoreCache.BuildKey(request.Lat, request.Lng, cappedRadius);
        var result = await _riskScoreCache.GetOrComputeAsync(
            cacheKey,
            async token =>
            {
                var hazards = await _hazardQueries.LoadHazardsNearPointAsync(request.Lat, request.Lng, cappedRadius, token);
                return await _risk.EvaluateRiskAsync(request.Lat, request.Lng, cappedRadius, hazards);
            },
            cancellationToken);

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
        var hazards = await _hazardQueries.LoadHazardsNearPointAsync(request.Lat, request.Lng, request.Radius, cancellationToken);
        var result = await _aiRisk.EvaluateSegmentRiskAsync(request.Lat, request.Lng, hazards, request.Radius);
        return Ok(result);
    }

    /// <summary>
    /// Alternative blend: full risk breakdown plus time-of-day and live weather weights.
    /// Prefer <c>ai-risk-score</c> for the logistic multi-factor model used in routing.
    /// </summary>
    [HttpGet("hazard-blend-risk")]
    [ProducesResponseType(typeof(PredictiveRiskResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PredictiveRiskResult>> GetHazardBlendRisk(
        [FromQuery] RiskScoreRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var hazards = await _hazardQueries.LoadHazardsNearPointAsync(request.Lat, request.Lng, request.Radius, cancellationToken);
        var result = await _risk.PredictRiskAsync(request.Lat, request.Lng, request.Radius, hazards);
        return Ok(result);
    }

    private void RecordSafePath(Stopwatch stopwatch, string outcome) =>
        _metrics.SafePathCompleted(
            "/api/v{version}/routing/safe-path",
            outcome,
            stopwatch.Elapsed.TotalMilliseconds);

    private void RecordSafePathOptions(Stopwatch stopwatch, string outcome) =>
        _metrics.SafePathCompleted(
            "/api/v{version}/routing/safe-path/options",
            outcome,
            stopwatch.Elapsed.TotalMilliseconds);
}
