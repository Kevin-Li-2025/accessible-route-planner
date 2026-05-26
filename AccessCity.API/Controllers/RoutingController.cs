using System.Diagnostics;
using Asp.Versioning;
using AccessCity.API.Common;
using AccessCity.API.Configuration;
using AccessCity.API.Exceptions;
using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using AccessCity.API.Security;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    private readonly IRouteOptionsCacheService _routeOptionsCache;
    private readonly IRiskScoreCacheService _riskScoreCache;
    private readonly IRouteGraphStatusService _routeGraphStatus;
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
        IRouteOptionsCacheService routeOptionsCache,
        IRiskScoreCacheService riskScoreCache,
        IRouteGraphStatusService routeGraphStatus,
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
        _routeOptionsCache = routeOptionsCache;
        _riskScoreCache = riskScoreCache;
        _routeGraphStatus = routeGraphStatus;
        _metrics = metrics;
        _routingOptions = routingOptions.Value;
    }

    /// <summary>
    /// Computes a risk-aware safe path between two coordinates.
    /// Uses request coalescing to deduplicate identical concurrent requests.
    /// Returns 504 if computation exceeds 30 seconds.
    /// </summary>
    [HttpPost("safe-path")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.RoutingHeavy)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.RouteSync)]
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
                List<HazardReport>? hazards = null;
                var probeBudget = GetAsyncFirstCacheProbeBudget();
                if (probeBudget > TimeSpan.Zero)
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    probeCts.CancelAfter(probeBudget);
                    try
                    {
                        hazards = await _hazardQueries.LoadHazardsForRouteAsync(request, probeCts.Token);
                        var contextFingerprint = await BuildRouteContextFingerprintAsync(hazards, probeCts.Token);
                        var cacheKey = _routeCache.BuildKey(
                            request.Start.Y,
                            request.Start.X,
                            request.End.Y,
                            request.End.X,
                            request.Profile ?? "standard",
                            request.SafetyWeight,
                            request.Preferences,
                            contextFingerprint);
                        var cached = await _routeCache.TryGetAsync(cacheKey);
                        if (cached is not null)
                        {
                            RecordSafePath(stopwatch, "cache_hit");
                            return Ok(cached);
                        }
                    }
                    catch (OperationCanceledException) when (probeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        hazards = null;
                    }
                }

                var jobId = await _jobs.SubmitAsync(request, hazards, cancellationToken);
                var completedJob = await _jobs.GetResultAsync(jobId, cancellationToken);
                if (completedJob?.Status == RouteJobStatus.Completed && completedJob.Route is not null)
                {
                    RecordSafePath(stopwatch, "completed_job_hit");
                    return Ok(completedJob.Route);
                }

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
                }).WaitAsync(timeoutCts.Token);

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
    [EnableRateLimiting(AccessCityRateLimitPolicies.RoutingHeavy)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.RouteAsyncSubmit)]
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
    [EnableRateLimiting(AccessCityRateLimitPolicies.RoutingPoll)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.ShortRead)]
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
    /// Reports whether imported OSM route graph coverage is available for accessibility-aware routing.
    /// </summary>
    [HttpGet("route-graph/status")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.HotRead)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.ShortRead)]
    [ProducesResponseType(typeof(RouteGraphCoverageStatus), StatusCodes.Status200OK)]
    public async Task<ActionResult<RouteGraphCoverageStatus>> GetRouteGraphStatus(CancellationToken cancellationToken)
    {
        return Ok(await _routeGraphStatus.GetStatusAsync(cancellationToken));
    }

    /// <summary>
    /// Multi-objective routing: same recommendation as <c>safe-path</c> when OSRM is used, plus up to three labelled
    /// alternatives (shortest distance, lowest composite risk, fastest time) when the router returns multiple geometries.
    /// </summary>
    [HttpPost("safe-path/options")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.RoutingHeavy)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.RouteSync)]
    [ProducesResponseType(typeof(SafePathOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SafePathOptionsResponse>> GetSafePathOptions(
        [FromBody] RouteRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (_routingOptions.AsyncFirstForCacheMiss)
        {
            List<HazardReport>? asyncHazards = null;
            var probeBudget = GetAsyncFirstCacheProbeBudget();
            if (probeBudget > TimeSpan.Zero)
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCts.CancelAfter(probeBudget);
                try
                {
                    asyncHazards = await _hazardQueries.LoadHazardsForRouteAsync(request, probeCts.Token);
                    var contextFingerprint = await BuildRouteContextFingerprintAsync(asyncHazards, probeCts.Token);
                    var cacheKey = _routeOptionsCache.BuildKey(
                        request.Start.Y,
                        request.Start.X,
                        request.End.Y,
                        request.End.X,
                        request.Profile ?? "standard",
                        request.SafetyWeight,
                        request.Preferences,
                        contextFingerprint);
                    var cached = await _routeOptionsCache.TryGetAsync(cacheKey);
                    if (cached is not null)
                    {
                        RecordSafePathOptions(stopwatch, "cache_hit");
                        return Ok(cached);
                    }
                }
                catch (OperationCanceledException) when (probeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    asyncHazards = null;
                }
            }

            var jobId = await _jobs.SubmitOptionsAsync(request, asyncHazards, cancellationToken);
            var completedJob = await _jobs.GetResultAsync(jobId, cancellationToken);
            if (completedJob?.Status == RouteJobStatus.Completed && completedJob.Options is not null)
            {
                RecordSafePathOptions(stopwatch, "completed_job_hit");
                return Ok(completedJob.Options);
            }

            RecordSafePathOptions(stopwatch, "async_accepted");
            return Accepted(new
            {
                jobId,
                kind = RouteJobKind.SafePathOptions.ToString(),
                status = "pending",
                pollUrl = $"/api/v1/routing/jobs/{jobId}",
                detail = "Route options queued; poll the job endpoint for recommended and variant routes."
            });
        }

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, _routingOptions.SyncSafePathTimeoutSeconds)));
        var workToken = timeoutCts.Token;

        try
        {
            var queueTimeout = TimeSpan.FromSeconds(Math.Max(1, _routingOptions.ComputationQueueTimeoutSeconds));
            if (UsesPrimaryRouteOnlyOptions(request))
            {
                var primaryRoute = await _coalescing.GetOrComputeAsync(
                    request,
                    async () =>
                    {
                        await using var lease = await _routeLimiter.TryAcquireAsync(queueTimeout, workToken);
                        if (lease is null)
                        {
                            return null;
                        }

                        var hazards = await _hazardQueries.LoadHazardsForRouteAsync(request, workToken);
                        return await _routing.FindSafePathAsync(request, hazards, workToken);
                    }).WaitAsync(workToken);
                if (primaryRoute is null)
                {
                    RecordSafePathOptions(stopwatch, "capacity_saturated");
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError(
                        "Route computation capacity is saturated.",
                        Detail: "Retry shortly or use the async job endpoint for the primary route."));
                }

                var response = new SafePathOptionsResponse
                {
                    Recommended = primaryRoute,
                    Variants = new List<RoutedOptionVariant>()
                };

                RecordSafePathOptions(stopwatch, "primary_route_only");
                return Ok(response);
            }

            var outcome = "success";
            var result = await _coalescing.GetOrComputeOptionsAsync(
                request,
                async () =>
                {
                    var hazards = await _hazardQueries.LoadHazardsForRouteAsync(request, workToken);
                    var contextFingerprint = await BuildRouteContextFingerprintAsync(hazards, workToken);
                    var cacheKey = _routeOptionsCache.BuildKey(
                        request.Start.Y,
                        request.Start.X,
                        request.End.Y,
                        request.End.X,
                        request.Profile ?? "standard",
                        request.SafetyWeight,
                        request.Preferences,
                        contextFingerprint);
                    var cached = await _routeOptionsCache.TryGetAsync(cacheKey);
                    if (cached is not null)
                    {
                        outcome = "cache_hit";
                        return cached;
                    }

                    await using var lease = await _routeLimiter.TryAcquireAsync(queueTimeout, workToken);
                    if (lease is null)
                    {
                        return null;
                    }

                    var computed = await _routing.FindSafePathWithVariantsAsync(request, hazards, workToken);
                    await _routeOptionsCache.SetAsync(cacheKey, computed);
                    return computed;
                }).WaitAsync(workToken);

            if (result is null)
            {
                RecordSafePathOptions(stopwatch, "capacity_saturated");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError(
                    "Route computation capacity is saturated.",
                    Detail: "Retry shortly or use the async job endpoint for the primary route."));
            }

            RecordSafePathOptions(stopwatch, outcome);
            return Ok(result);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            RecordSafePathOptions(stopwatch, "timeout");
            return StatusCode(StatusCodes.Status504GatewayTimeout, new ApiError(
                "Route options computation timed out.",
                Detail: "The route options computation exceeded the synchronous route timeout. Consider using the async job endpoint."));
        }
    }

    /// <summary>
    /// Evaluates the composite risk score for a given coordinate within a specified radius.
    /// </summary>
    [HttpGet("risk-score")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.HotRead)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.ShortRead)]
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
    /// Returns a deterministic predictive risk score with time-of-day and weather factors.
    /// </summary>
    [HttpGet("ai-risk-score")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.HotRead)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.ShortRead)]
    [ProducesResponseType(typeof(PredictiveRiskResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PredictiveRiskResult>> GetAiRiskScore(
        [FromQuery] RiskScoreRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var cappedRadius = Math.Min(
            Math.Max(0, request.Radius),
            Math.Max(1, _routingOptions.MaxRiskQueryRadiusMetres));
        var cacheKey = _riskScoreCache.BuildKey("ai-risk-score", request.Lat, request.Lng, cappedRadius);
        var result = await _riskScoreCache.GetOrComputeAsync(
            cacheKey,
            async token =>
            {
                var hazards = await _hazardQueries.LoadHazardsNearPointAsync(request.Lat, request.Lng, cappedRadius, token);
                return await _aiRisk.EvaluateSegmentRiskAsync(request.Lat, request.Lng, hazards, cappedRadius);
            },
            cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Alternative blend: full risk breakdown plus time-of-day and live weather weights.
    /// Prefer <c>ai-risk-score</c> for the deterministic multi-factor model used in routing.
    /// </summary>
    [HttpGet("hazard-blend-risk")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.HotRead)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.ShortRead)]
    [ProducesResponseType(typeof(PredictiveRiskResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PredictiveRiskResult>> GetHazardBlendRisk(
        [FromQuery] RiskScoreRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var cappedRadius = Math.Min(
            Math.Max(0, request.Radius),
            Math.Max(1, _routingOptions.MaxRiskQueryRadiusMetres));
        var cacheKey = _riskScoreCache.BuildKey("hazard-blend-risk", request.Lat, request.Lng, cappedRadius);
        var result = await _riskScoreCache.GetOrComputeAsync(
            cacheKey,
            async token =>
            {
                var hazards = await _hazardQueries.LoadHazardsNearPointAsync(request.Lat, request.Lng, cappedRadius, token);
                return await _risk.PredictRiskAsync(request.Lat, request.Lng, cappedRadius, hazards);
            },
            cancellationToken);
        return Ok(result);
    }

    private async Task<string> BuildRouteContextFingerprintAsync(
        IEnumerable<HazardReport> hazards,
        CancellationToken cancellationToken)
    {
        var hazardContext = RouteRequestFingerprint.HazardContext(hazards);
        var graphVersion = await _routeGraphStatus.GetVersionAsync(cancellationToken);
        return $"{hazardContext}:graph:{graphVersion}";
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

    private static bool UsesPrimaryRouteOnlyOptions(RouteRequest request)
    {
        if (string.Equals(request.Profile, "manual-wheelchair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Profile, "power-wheelchair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Profile, "stroller", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return request.Preferences?.Any(preference =>
            string.Equals(preference, "wheelchair", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private TimeSpan GetAsyncFirstCacheProbeBudget()
    {
        var milliseconds = Math.Clamp(_routingOptions.AsyncFirstCacheProbeMilliseconds, 0, 1_000);
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
