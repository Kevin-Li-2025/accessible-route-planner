using Asp.Versioning;
using AccessCity.API.Common;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Exceptions;
using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    private readonly RoutingService _routing;
    private readonly RiskScoringService _risk;
    private readonly PredictiveRiskModel _aiRisk;
    private readonly AppDbContext _dbContext;
    private readonly IRouteJobService _jobs;
    private readonly IRouteCoalescingService _coalescing;
    private readonly IRouteComputationLimiter _routeLimiter;
    private readonly IRiskScoreCacheService _riskScoreCache;
    private readonly RoutingOptions _routingOptions;

    public RoutingController(
        RoutingService routing,
        RiskScoringService risk,
        PredictiveRiskModel aiRisk,
        AppDbContext dbContext,
        IRouteJobService jobs,
        IRouteCoalescingService coalescing,
        IRouteComputationLimiter routeLimiter,
        IRiskScoreCacheService riskScoreCache,
        IOptions<RoutingOptions> routingOptions)
    {
        _routing = routing;
        _risk = risk;
        _aiRisk = aiRisk;
        _dbContext = dbContext;
        _jobs = jobs;
        _coalescing = coalescing;
        _routeLimiter = routeLimiter;
        _riskScoreCache = riskScoreCache;
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
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<RouteResponse>> GetSafePath(
        [FromBody] RouteRequest request,
        CancellationToken cancellationToken)
    {
        // Enforce a short synchronous timeout; heavier work belongs on the async job path.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var safePathTimeout = TimeSpan.FromSeconds(Math.Max(1, _routingOptions.SyncSafePathTimeoutSeconds));
        timeoutCts.CancelAfter(safePathTimeout);

        try
        {
            var queueTimeout = TimeSpan.FromSeconds(Math.Max(1, _routingOptions.ComputationQueueTimeoutSeconds));
            var result = await _coalescing.GetOrComputeAsync(
                request,
                async () =>
                {
                    await using var lease = await _routeLimiter.TryAcquireAsync(queueTimeout, timeoutCts.Token)
                        ?? throw new RouteCapacityExceededException();

                    var hazards = await LoadHazardsForRouteAsync(request, timeoutCts.Token);
                    return await _routing.FindSafePathAsync(request, hazards, timeoutCts.Token);
                });

            if (result is null)
            {
                return NotFound(new ApiError(
                    "No route found.",
                    Detail: "The routing engine could not find a path. Ensure the chosen area is supported."));
            }

            return Ok(result);
        }
        catch (RouteCapacityExceededException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError(
                "Route computation capacity is saturated.",
                Detail: "Retry shortly or use the async job endpoint: POST /routing/safe-path/async."));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
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
        var hazards = await LoadHazardsForRouteAsync(request, cancellationToken);
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
        var queueTimeout = TimeSpan.FromSeconds(Math.Max(1, _routingOptions.ComputationQueueTimeoutSeconds));
        await using var lease = await _routeLimiter.TryAcquireAsync(queueTimeout, cancellationToken);
        if (lease is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError(
                "Route computation capacity is saturated.",
                Detail: "Retry shortly or use the async job endpoint for the primary route."));
        }

        var hazards = await LoadHazardsForRouteAsync(request, cancellationToken);
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
        var cappedRadius = Math.Min(
            Math.Max(0, request.Radius),
            Math.Max(1, _routingOptions.MaxRiskQueryRadiusMetres));
        var cacheKey = _riskScoreCache.BuildKey(request.Lat, request.Lng, cappedRadius);
        var result = await _riskScoreCache.GetOrComputeAsync(
            cacheKey,
            async token =>
            {
                var hazards = await LoadHazardsNearPointAsync(request.Lat, request.Lng, cappedRadius, token);
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
        var hazards = await LoadHazardsNearPointAsync(request.Lat, request.Lng, request.Radius, cancellationToken);
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
        var hazards = await LoadHazardsNearPointAsync(request.Lat, request.Lng, request.Radius, cancellationToken);
        var result = await _risk.PredictRiskAsync(request.Lat, request.Lng, request.Radius, hazards);
        return Ok(result);
    }

    private async Task<List<HazardReport>> LoadHazardsForRouteAsync(
        RouteRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Start is null || request.End is null)
        {
            return await LoadActiveHazardsAsync(cancellationToken);
        }

        var paddingMetres = Math.Max(0, _routingOptions.HazardQueryPaddingMetres);
        var latitudePadding = MetresToLatitudeDegrees(paddingMetres);
        var centerLatitude = (request.Start.Y + request.End.Y) / 2.0;
        var longitudePadding = MetresToLongitudeDegrees(paddingMetres, centerLatitude);

        var minLon = Math.Min(request.Start.X, request.End.X) - longitudePadding;
        var maxLon = Math.Max(request.Start.X, request.End.X) + longitudePadding;
        var minLat = Math.Min(request.Start.Y, request.End.Y) - latitudePadding;
        var maxLat = Math.Max(request.Start.Y, request.End.Y) + latitudePadding;
        var limit = Math.Max(1, _routingOptions.MaxHazardsPerRequest);

        if (_dbContext.Database.IsRelational())
        {
            return await _dbContext.Hazards
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM public.hazard_report
                    WHERE status IN ('reported'::hazard_status, 'under_review'::hazard_status)
                      AND geom && ST_MakeEnvelope({minLon}, {minLat}, {maxLon}, {maxLat}, 4326)
                    ORDER BY reported_at DESC
                    LIMIT {limit}
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        return (await LoadActiveHazardsAsync(cancellationToken))
            .Where(h => h.Location is not null
                        && h.Location.X >= minLon
                        && h.Location.X <= maxLon
                        && h.Location.Y >= minLat
                        && h.Location.Y <= maxLat)
            .Take(limit)
            .ToList();
    }

    private async Task<List<HazardReport>> LoadHazardsNearPointAsync(
        double latitude,
        double longitude,
        double radiusMetres,
        CancellationToken cancellationToken)
    {
        var cappedRadius = Math.Min(
            Math.Max(0, radiusMetres),
            Math.Max(1, _routingOptions.MaxRiskQueryRadiusMetres));
        var queryRadius = cappedRadius + Math.Max(0, _routingOptions.HazardQueryPaddingMetres);
        var limit = Math.Max(1, _routingOptions.MaxHazardsPerRequest);

        if (_dbContext.Database.IsRelational())
        {
            return await _dbContext.Hazards
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM public.hazard_report
                    WHERE status IN ('reported'::hazard_status, 'under_review'::hazard_status)
                      AND ST_DWithin(
                          geom::geography,
                          ST_SetSRID(ST_MakePoint({longitude}, {latitude}), 4326)::geography,
                          {queryRadius})
                    ORDER BY reported_at DESC
                    LIMIT {limit}
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        return (await LoadActiveHazardsAsync(cancellationToken))
            .Where(h => h.Location is not null
                        && HaversineMetres(latitude, longitude, h.Location.Y, h.Location.X) <= queryRadius)
            .Take(limit)
            .ToList();
    }

    private async Task<List<HazardReport>> LoadActiveHazardsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Hazards
            .Where(h => h.Status == HazardStatus.Reported || h.Status == HazardStatus.UnderReview)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    private static double MetresToLatitudeDegrees(double metres) => metres / 111_320.0;

    private static double MetresToLongitudeDegrees(double metres, double latitude)
    {
        var radians = latitude * Math.PI / 180.0;
        var metresPerDegree = 111_320.0 * Math.Max(0.1, Math.Cos(radians));
        return metres / metresPerDegree;
    }

    private static double HaversineMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6_371_000.0;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadius * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
