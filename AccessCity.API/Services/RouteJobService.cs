using System.Collections.Concurrent;
using AccessCity.API.Models;

namespace AccessCity.API.Services;

/// <summary>
/// In-memory route job queue implementing the Job-Status pattern (RFC 7240 / 202 Accepted).
/// Clients submit a route request, receive a job ID, and poll for the result.
/// This decouples the expensive A* computation from the HTTP request lifecycle,
/// preventing database connection pool exhaustion under concurrent load.
/// </summary>
public interface IRouteJobService
{
    /// <summary>Submits a route computation and returns a job ID immediately.</summary>
    string Submit(RouteRequest request, List<HazardReport> hazards);

    /// <summary>Polls the status of a previously submitted job.</summary>
    RouteJobResult? GetResult(string jobId);
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
    private readonly ILogger<RouteJobService> _logger;

    // Limit concurrent A* computations to prevent DB pool exhaustion.
    private readonly SemaphoreSlim _concurrencyGate = new(4, 4);

    public RouteJobService(IServiceScopeFactory scopeFactory, IRouteCoalescingService coalescing, ILogger<RouteJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _coalescing = coalescing;
        _logger = logger;
    }

    public string Submit(RouteRequest request, List<HazardReport> hazards)
    {
        var jobId = Guid.NewGuid().ToString("N")[..12];
        var result = new RouteJobResult
        {
            JobId = jobId,
            Status = RouteJobStatus.Pending,
            SubmittedAt = DateTime.UtcNow
        };

        _jobs[jobId] = result;

        // Fire-and-forget the computation on the thread pool.
        _ = ComputeAsync(jobId, request, hazards);

        return jobId;
    }

    public RouteJobResult? GetResult(string jobId) =>
        _jobs.TryGetValue(jobId, out var result) ? result : null;

    private async Task ComputeAsync(string jobId, RouteRequest request, List<HazardReport> hazards)
    {
        var result = _jobs[jobId];
        result.Status = RouteJobStatus.Processing;

        try
        {
            await _concurrencyGate.WaitAsync();
            try
            {
                // Create a scoped DI container to resolve Scoped services (RoutingService, AppDbContext).
                await using var scope = _scopeFactory.CreateAsyncScope();
                var routing = scope.ServiceProvider.GetRequiredService<RoutingService>();

                // Try the coalescing layer first — identical requests share a single computation.
                var route = await _coalescing.GetOrComputeAsync(
                    request,
                    async () => await routing.FindSafePathAsync(request, hazards));

                result.Route = route;
                result.Status = route is not null ? RouteJobStatus.Completed : RouteJobStatus.Failed;
                result.Error = route is null ? "No route found." : null;
            }
            finally
            {
                _concurrencyGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Route job {JobId} failed", jobId);
            result.Status = RouteJobStatus.Failed;
            result.Error = "Route computation failed.";
        }

        result.CompletedAt = DateTime.UtcNow;

        // Auto-expire completed jobs after 5 minutes.
        _ = CleanupAfterDelay(jobId, TimeSpan.FromMinutes(5));
    }

    private async Task CleanupAfterDelay(string jobId, TimeSpan delay)
    {
        await Task.Delay(delay);
        _jobs.TryRemove(jobId, out _);
    }
}
