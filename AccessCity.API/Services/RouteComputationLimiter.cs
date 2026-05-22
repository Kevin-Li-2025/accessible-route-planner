using AccessCity.API.Configuration;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Services;

public interface IRouteComputationLimiter
{
    ValueTask<RouteComputationLease?> TryAcquireAsync(TimeSpan waitTimeout, CancellationToken cancellationToken);
}

public sealed class RouteComputationLimiter : IRouteComputationLimiter
{
    private readonly SemaphoreSlim _semaphore;

    public RouteComputationLimiter(IOptions<RoutingOptions> options)
    {
        var maxConcurrency = Math.Max(1, options.Value.MaxConcurrentComputations);
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public async ValueTask<RouteComputationLease?> TryAcquireAsync(
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var acquired = await _semaphore.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false);
        return acquired ? new RouteComputationLease(_semaphore) : null;
    }
}

public sealed class RouteComputationLease : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private int _released;

    internal RouteComputationLease(SemaphoreSlim semaphore)
    {
        _semaphore = semaphore;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _semaphore.Release();
        }
    }
}
