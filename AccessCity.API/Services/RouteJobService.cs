using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AccessCity.API.Configuration;
using AccessCity.API.Exceptions;
using AccessCity.API.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetTopologySuite.IO.Converters;

namespace AccessCity.API.Services;

/// <summary>
/// Distributed-cache backed route job queue implementing the Job-Status pattern (RFC 7240 / 202 Accepted).
/// Clients submit a route request, receive a job ID, and poll for the result.
/// This decouples the expensive A* computation from the HTTP request lifecycle,
/// preventing database connection pool exhaustion under concurrent load while allowing polling across API replicas.
/// </summary>
public interface IRouteJobService
{
    /// <summary>Submits a route computation and returns a job ID immediately.</summary>
    Task<string> SubmitAsync(RouteRequest request, List<HazardReport>? hazards = null, CancellationToken cancellationToken = default);

    /// <summary>Polls the status of a previously submitted job.</summary>
    Task<RouteJobResult?> GetResultAsync(string jobId, CancellationToken cancellationToken = default);
}

/// <summary>Result envelope for an async route job.</summary>
public sealed class RouteJobResult
{
    public required string JobId { get; init; }
    public RouteJobStatus Status { get; set; }
    public RouteResponse? Route { get; set; }
    public string? Error { get; set; }
    public DateTime SubmittedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
}

public enum RouteJobStatus { Pending, Processing, Completed, Failed }

public sealed class RouteJobService : IRouteJobService
{
    private readonly ConcurrentDictionary<string, RouteJobResult> _jobs = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRouteCoalescingService _coalescing;
    private readonly IRouteComputationLimiter _routeLimiter;
    private readonly IDistributedCache _distributedCache;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RouteJobService> _logger;
    private readonly RoutingOptions _options;
    private static readonly TimeSpan JobTtl = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JobJsonOptions = CreateJobJsonOptions();

    public RouteJobService(
        IServiceScopeFactory scopeFactory,
        IRouteCoalescingService coalescing,
        IRouteComputationLimiter routeLimiter,
        IDistributedCache distributedCache,
        IHostApplicationLifetime lifetime,
        IOptions<RoutingOptions> options,
        ILogger<RouteJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _coalescing = coalescing;
        _routeLimiter = routeLimiter;
        _distributedCache = distributedCache;
        _lifetime = lifetime;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> SubmitAsync(
        RouteRequest request,
        List<HazardReport>? hazards = null,
        CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid().ToString("N")[..12];
        var result = new RouteJobResult
        {
            JobId = jobId,
            Status = RouteJobStatus.Pending,
            SubmittedAt = DateTime.UtcNow
        };

        _jobs[jobId] = result;
        await PersistAsync(result, cancellationToken);

        // Fire-and-forget the computation on the thread pool.
        _ = Task.Run(
            () => ComputeAsync(jobId, request, hazards, _lifetime.ApplicationStopping),
            CancellationToken.None);

        return jobId;
    }

    public async Task<RouteJobResult?> GetResultAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (_jobs.TryGetValue(jobId, out var result))
        {
            return result;
        }

        var json = await _distributedCache.GetStringAsync(JobCacheKey(jobId), cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RouteJobResult>(json, JobJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Route job {JobId} could not be deserialized from distributed cache", jobId);
            return null;
        }
    }

    private async Task ComputeAsync(
        string jobId,
        RouteRequest request,
        List<HazardReport>? hazards,
        CancellationToken stoppingToken)
    {
        var result = _jobs[jobId];
        result.Status = RouteJobStatus.Processing;
        await PersistAsync(result, CancellationToken.None);

        try
        {
            var waitTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.JobComputationQueueTimeoutSeconds));

            // Try the coalescing layer first; identical requests share a single computation and one limiter lease.
            var route = await _coalescing.GetOrComputeAsync(
                request,
                async () =>
                {
                    await using var lease = await _routeLimiter.TryAcquireAsync(waitTimeout, stoppingToken)
                        ?? throw new RouteCapacityExceededException();

                    // Create a scoped DI container to resolve scoped routing dependencies.
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var scopedHazards = hazards;
                    if (scopedHazards is null)
                    {
                        var hazardQueries = scope.ServiceProvider.GetRequiredService<IHazardQueryService>();
                        scopedHazards = await hazardQueries.LoadHazardsForRouteAsync(request, stoppingToken);
                    }

                    var routing = scope.ServiceProvider.GetRequiredService<IRoutingService>();
                    return await routing.FindSafePathAsync(request, scopedHazards, stoppingToken);
                });

            result.Route = route;
            result.Status = route is not null ? RouteJobStatus.Completed : RouteJobStatus.Failed;
            result.Error = route is null ? "No route found." : null;
        }
        catch (RouteCapacityExceededException ex)
        {
            _logger.LogWarning(ex, "Route job {JobId} exceeded route computation capacity", jobId);
            result.Status = RouteJobStatus.Failed;
            result.Error = "Route computation capacity is saturated. Retry later.";
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            result.Status = RouteJobStatus.Failed;
            result.Error = "Route computation was cancelled during application shutdown.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Route job {JobId} failed", jobId);
            result.Status = RouteJobStatus.Failed;
            result.Error = "Route computation failed.";
        }

        result.CompletedAt = DateTime.UtcNow;
        await PersistAsync(result, CancellationToken.None);

        // Auto-expire completed jobs after 5 minutes.
        _ = CleanupAfterDelay(jobId, JobTtl, stoppingToken);
    }

    private async Task CleanupAfterDelay(string jobId, TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        _jobs.TryRemove(jobId, out _);
        try
        {
            await _distributedCache.RemoveAsync(JobCacheKey(jobId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Route job {JobId} could not be removed from distributed cache", jobId);
        }
    }

    private async Task PersistAsync(RouteJobResult result, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(result, JobJsonOptions);
            await _distributedCache.SetStringAsync(
                JobCacheKey(result.JobId),
                json,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = JobTtl },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Route job {JobId} could not be persisted to distributed cache", result.JobId);
        }
    }

    private static string JobCacheKey(string jobId) => $"route_job:{jobId}";

    private static JsonSerializerOptions CreateJobJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        options.Converters.Add(new GeoJsonConverterFactory());
        return options;
    }
}
