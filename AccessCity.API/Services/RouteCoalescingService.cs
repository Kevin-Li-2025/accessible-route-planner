using System.Collections.Concurrent;
using AccessCity.API.Models;

namespace AccessCity.API.Services;

/// <summary>
/// Coalesces identical route computation requests within a configurable time window.
/// If multiple clients request the same origin→destination within 500ms, only one
/// A* computation is executed and the result is shared among all waiters.
/// This dramatically reduces redundant PostGIS load under concurrent traffic.
/// </summary>
public interface IRouteCoalescingService
{
    /// <summary>
    /// Returns a cached or in-flight result for the given request.
    /// If no computation is in progress, starts one using <paramref name="factory"/>.
    /// </summary>
    Task<RouteResponse?> GetOrComputeAsync(RouteRequest request, Func<Task<RouteResponse?>> factory);
}

public sealed class RouteCoalescingService : IRouteCoalescingService
{
    private readonly ConcurrentDictionary<string, CoalescedEntry> _inflight = new();
    private readonly ILogger<RouteCoalescingService> _logger;

    public RouteCoalescingService(ILogger<RouteCoalescingService> logger)
    {
        _logger = logger;
    }

    public async Task<RouteResponse?> GetOrComputeAsync(RouteRequest request, Func<Task<RouteResponse?>> factory)
    {
        var key = BuildKey(request);

        // Fast path: an identical request is already being computed.
        if (_inflight.TryGetValue(key, out var existing) && !existing.IsExpired)
        {
            _logger.LogDebug("Route request coalesced for key {Key}", key);
            return await existing.Task;
        }

        // Slow path: we are the first requester — start the computation.
        var tcs = new TaskCompletionSource<RouteResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new CoalescedEntry(tcs.Task, DateTime.UtcNow);

        if (_inflight.TryAdd(key, entry))
        {
            try
            {
                var result = await factory();
                tcs.SetResult(result);
                return result;
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
                throw;
            }
            finally
            {
                // Allow re-computation after a short window.
                _ = RemoveAfterDelay(key, TimeSpan.FromMilliseconds(500));
            }
        }

        // Another thread won the race — join their computation.
        if (_inflight.TryGetValue(key, out var raceWinner))
        {
            return await raceWinner.Task;
        }

        // Extreme edge case: entry was already cleaned up.
        return await factory();
    }

    private static string BuildKey(RouteRequest request) =>
        $"{request.Start?.X:F5},{request.Start?.Y:F5}->{request.End?.X:F5},{request.End?.Y:F5}|{request.Profile}|{request.SafetyWeight:F2}";

    private async Task RemoveAfterDelay(string key, TimeSpan delay)
    {
        await Task.Delay(delay);
        _inflight.TryRemove(key, out _);
    }

    private sealed record CoalescedEntry(Task<RouteResponse?> Task, DateTime CreatedAt)
    {
        /// <summary>Entries older than 30 seconds are considered expired.</summary>
        public bool IsExpired => DateTime.UtcNow - CreatedAt > TimeSpan.FromSeconds(30);
    }
}
