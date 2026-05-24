using AccessCity.API.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Services.Background;

public sealed class RouteGraphArtifactManifestWarmupBackgroundService : BackgroundService
{
    private readonly IRouteGraphArtifactStore _artifactStore;
    private readonly IDistributedCache _distributedCache;
    private readonly RoutingOptions _options;
    private readonly ILogger<RouteGraphArtifactManifestWarmupBackgroundService> _logger;

    public RouteGraphArtifactManifestWarmupBackgroundService(
        IRouteGraphArtifactStore artifactStore,
        IDistributedCache distributedCache,
        IOptions<RoutingOptions> options,
        ILogger<RouteGraphArtifactManifestWarmupBackgroundService> logger)
    {
        _artifactStore = artifactStore;
        _distributedCache = distributedCache;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ShouldWarm())
        {
            return;
        }

        try
        {
            var delay = TimeSpan.FromSeconds(Math.Clamp(_options.RouteGraphFileArtifactWarmupDelaySeconds, 0, 300));
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, stoppingToken);
            }

            var result = await WarmManifestAsync(stoppingToken);
            if (result.SkippedReason is not null)
            {
                _logger.LogInformation("Route graph artifact manifest warmup skipped: {Reason}.", result.SkippedReason);
                return;
            }

            _logger.LogInformation(
                "Warmed {WarmedShardCount}/{AttemptedShardCount} route graph artifact shards into distributed cache ({PayloadBytes} bytes).",
                result.WarmedShardCount,
                result.AttemptedShardCount,
                result.PayloadBytes);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task<RouteGraphArtifactManifestWarmupResult> WarmManifestAsync(
        CancellationToken cancellationToken = default)
    {
        if (!ShouldWarm())
        {
            return RouteGraphArtifactManifestWarmupResult.Skipped("disabled");
        }

        var manifest = await _artifactStore.TryReadManifestAsync(cancellationToken);
        if (manifest is null || manifest.Shards.Length == 0)
        {
            return RouteGraphArtifactManifestWarmupResult.Skipped("manifest-missing");
        }

        var shards = SelectWarmupShards(manifest).ToArray();
        var ttl = TimeSpan.FromSeconds(Math.Max(30, _options.RouteGraphCacheTtlSeconds));
        var warmed = 0;
        var bytes = 0L;

        foreach (var shard in shards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await _artifactStore.TryReadManifestShardAsync(shard, cancellationToken);
            if (read?.Payload is null || read.Payload.Length == 0)
            {
                continue;
            }

            await _distributedCache.SetAsync(
                shard.CacheKey,
                read.Payload,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                cancellationToken);

            warmed++;
            bytes += read.PayloadBytes;
        }

        return new RouteGraphArtifactManifestWarmupResult(
            manifest.Shards.Length,
            shards.Length,
            warmed,
            bytes,
            SkippedReason: null);
    }

    private bool ShouldWarm() =>
        _options.RouteGraphFileArtifactWarmupEnabled
        && _options.RouteGraphPackedArtifactsEnabled
        && _options.RouteGraphFileArtifactManifestEnabled
        && _artifactStore.IsEnabled;

    private IEnumerable<RouteGraphArtifactManifestShard> SelectWarmupShards(RouteGraphArtifactManifest manifest)
    {
        var shards = _options.RouteGraphFileArtifactWarmupLargestShardsFirst
            ? manifest.Shards.OrderByDescending(shard => shard.PayloadBytes).ThenBy(shard => shard.CacheKey, StringComparer.Ordinal)
            : manifest.Shards.OrderBy(shard => shard.CacheKey, StringComparer.Ordinal);

        var limit = _options.RouteGraphFileArtifactWarmupShardLimit;
        return limit <= 0 ? shards : shards.Take(limit);
    }
}

public sealed record RouteGraphArtifactManifestWarmupResult(
    int ManifestShardCount,
    int AttemptedShardCount,
    int WarmedShardCount,
    long PayloadBytes,
    string? SkippedReason)
{
    public static RouteGraphArtifactManifestWarmupResult Skipped(string reason) =>
        new(0, 0, 0, 0, reason);
}
