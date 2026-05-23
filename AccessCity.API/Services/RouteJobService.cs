using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using AccessCity.API.Configuration;
using AccessCity.API.Exceptions;
using AccessCity.API.Messaging;
using AccessCity.API.Models;
using AccessCity.API.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetTopologySuite.IO.Converters;
using StackExchange.Redis;

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

    /// <summary>Submits route-options computation and returns a job ID immediately.</summary>
    Task<string> SubmitOptionsAsync(RouteRequest request, List<HazardReport>? hazards = null, CancellationToken cancellationToken = default);

    /// <summary>Processes a queued route job. Called by the route worker consumer.</summary>
    Task ProcessQueuedJobAsync(RouteJobRequestedEvent @event, CancellationToken cancellationToken = default);

    /// <summary>Polls the status of a previously submitted job.</summary>
    Task<RouteJobResult?> GetResultAsync(string jobId, CancellationToken cancellationToken = default);
}

/// <summary>Result envelope for an async route job.</summary>
public sealed class RouteJobResult
{
    public required string JobId { get; init; }
    public RouteJobKind Kind { get; set; }
    public RouteJobStatus Status { get; set; }
    public RouteResponse? Route { get; set; }
    public SafePathOptionsResponse? Options { get; set; }
    public string? Error { get; set; }
    public DateTime SubmittedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
}

public enum RouteJobStatus { Pending, Processing, Completed, Failed }

public sealed record RouteJobDispatchWorkItem(
    string JobId,
    RouteJobKind Kind,
    RouteRequest Request,
    List<HazardReport>? Hazards,
    RouteJobResult Result,
    string DedupeKey,
    bool DedupeAlreadyReserved);

public interface IRouteJobDispatchQueue
{
    ValueTask<RouteJobDispatchWorkItem> DequeueDispatchAsync(CancellationToken cancellationToken);

    Task DispatchSubmissionAsync(RouteJobDispatchWorkItem workItem, CancellationToken cancellationToken);
}

public sealed class RouteJobService : IRouteJobService, IRouteJobDispatchQueue
{
    private readonly ConcurrentDictionary<string, RouteJobResult> _jobs = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inflightSubmissions = new();
    private readonly ConcurrentDictionary<string, string> _completedJobsByDedupeKey = new();
    private readonly Channel<RouteJobDispatchWorkItem> _dispatchQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessageBus _messageBus;
    private readonly IRouteCoalescingService _coalescing;
    private readonly IRouteComputationLimiter _routeLimiter;
    private readonly IDistributedCache _distributedCache;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RouteJobService> _logger;
    private readonly RoutingOptions _options;
    private readonly IConnectionMultiplexer? _redis;
    private readonly bool _dispatchJobsToWorker;
    private static readonly TimeSpan JobTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan JobDedupeTtl = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JobJsonOptions = CreateJobJsonOptions();

    public RouteJobService(
        IServiceScopeFactory scopeFactory,
        IMessageBus messageBus,
        IRouteCoalescingService coalescing,
        IRouteComputationLimiter routeLimiter,
        IDistributedCache distributedCache,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        IOptions<RoutingOptions> options,
        ILogger<RouteJobService> logger,
        IServiceProvider services)
    {
        _scopeFactory = scopeFactory;
        _messageBus = messageBus;
        _coalescing = coalescing;
        _routeLimiter = routeLimiter;
        _distributedCache = distributedCache;
        _lifetime = lifetime;
        _options = options.Value;
        _redis = services.GetService<IConnectionMultiplexer>();
        _dispatchJobsToWorker = _options.DispatchJobsToWorker || configuration.GetValue<bool>("Messaging:UseKafka");
        _logger = logger;
        _dispatchQueue = Channel.CreateUnbounded<RouteJobDispatchWorkItem>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public async Task<string> SubmitAsync(
        RouteRequest request,
        List<HazardReport>? hazards = null,
        CancellationToken cancellationToken = default)
    {
        return await SubmitCoreAsync(RouteJobKind.SafePath, request, hazards, cancellationToken);
    }

    public async Task<string> SubmitOptionsAsync(
        RouteRequest request,
        List<HazardReport>? hazards = null,
        CancellationToken cancellationToken = default)
    {
        return await SubmitCoreAsync(RouteJobKind.SafePathOptions, request, hazards, cancellationToken);
    }

    private async Task<string> SubmitCoreAsync(
        RouteJobKind kind,
        RouteRequest request,
        List<HazardReport>? hazards,
        CancellationToken cancellationToken)
    {
        var dedupeKey = BuildJobDedupeKey(kind, request);
        var lazy = _inflightSubmissions.GetOrAdd(
            dedupeKey,
            _ => new Lazy<Task<string>>(
                () => SubmitCoreUncoalescedAsync(kind, request, hazards, dedupeKey, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value;
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted
                                    && _inflightSubmissions.TryGetValue(dedupeKey, out var current)
                                    && ReferenceEquals(current, lazy))
            {
                _inflightSubmissions.TryRemove(dedupeKey, out _);
            }
        }
    }

    private async Task<string> SubmitCoreUncoalescedAsync(
        RouteJobKind kind,
        RouteRequest request,
        List<HazardReport>? hazards,
        string dedupeKey,
        CancellationToken cancellationToken)
    {
        var jobId = BuildDeterministicJobId(dedupeKey);
        var dedupeAlreadyReserved = false;
        if (_completedJobsByDedupeKey.TryGetValue(dedupeKey, out var completedJobId))
        {
            var completed = await GetResultAsync(completedJobId, cancellationToken);
            if (completed?.Status == RouteJobStatus.Completed)
            {
                return completedJobId;
            }

            _completedJobsByDedupeKey.TryRemove(dedupeKey, out _);
        }

        if (_redis is { IsConnected: true })
        {
            var database = _redis.GetDatabase();
            var existingJobId = await database.StringGetAsync(dedupeKey);
            if (existingJobId.HasValue)
            {
                return existingJobId.ToString();
            }

            dedupeAlreadyReserved = await database.StringSetAsync(dedupeKey, jobId, JobDedupeTtl, When.NotExists);
            if (!dedupeAlreadyReserved)
            {
                existingJobId = await database.StringGetAsync(dedupeKey);
                if (existingJobId.HasValue)
                {
                    return existingJobId.ToString();
                }
            }
        }

        var result = new RouteJobResult
        {
            JobId = jobId,
            Kind = kind,
            Status = RouteJobStatus.Pending,
            SubmittedAt = DateTime.UtcNow
        };

        _jobs[jobId] = result;
        await PersistAsync(result, cancellationToken);
        if (!_dispatchQueue.Writer.TryWrite(new RouteJobDispatchWorkItem(
                jobId,
                kind,
                request,
                hazards,
                result,
                dedupeKey,
                dedupeAlreadyReserved)))
        {
            throw new RouteCapacityExceededException();
        }

        return jobId;
    }

    public ValueTask<RouteJobDispatchWorkItem> DequeueDispatchAsync(CancellationToken cancellationToken) =>
        _dispatchQueue.Reader.ReadAsync(cancellationToken);

    public async Task DispatchSubmissionAsync(
        RouteJobDispatchWorkItem workItem,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_redis is { IsConnected: true })
            {
                var database = _redis.GetDatabase();
                var currentReservation = await database.StringGetAsync(workItem.DedupeKey);
                if (currentReservation.HasValue
                    && !string.Equals(currentReservation.ToString(), workItem.JobId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!currentReservation.HasValue && !workItem.DedupeAlreadyReserved)
                {
                    var reserved = await database.StringSetAsync(
                        workItem.DedupeKey,
                        workItem.JobId,
                        JobDedupeTtl,
                        When.NotExists);
                    if (!reserved)
                    {
                        return;
                    }
                }
            }

            await PersistAsync(workItem.Result, cancellationToken);

            if (_dispatchJobsToWorker)
            {
                await _messageBus.PublishAsync(
                    new RouteJobRequestedEvent(
                        workItem.JobId,
                        workItem.Request,
                        workItem.Result.SubmittedAt,
                        workItem.Kind),
                    cancellationToken);
                return;
            }

            await ComputeAsync(
                workItem.JobId,
                workItem.Kind,
                workItem.Request,
                workItem.Hazards,
                workItem.Result.SubmittedAt,
                _lifetime.ApplicationStopping);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown; leave the job pending so a retried Kafka message or client retry can recover.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Route job {JobId} could not be queued", workItem.JobId);
            workItem.Result.Status = RouteJobStatus.Failed;
            workItem.Result.Error = "Route job could not be queued.";
            workItem.Result.CompletedAt = DateTime.UtcNow;
            await PersistAsync(workItem.Result, CancellationToken.None);
        }
    }

    private static string BuildJobDedupeKey(RouteJobKind kind, RouteRequest request)
    {
        var prefs = RouteRequestFingerprint.CanonicalPreferences(request.Preferences);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"route_job:dedupe:{kind}:{request.Start?.X:F5},{request.Start?.Y:F5}->{request.End?.X:F5},{request.End?.Y:F5}|{request.Profile}|{request.SafetyWeight:F2}|prefs:{prefs}|{RouteRequestFingerprint.AlgorithmVersion}");
    }

    private static string BuildDeterministicJobId(string dedupeKey)
    {
        var bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)JobDedupeTtl.TotalSeconds;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{dedupeKey}:{bucket}"));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..12];
    }

    public async Task ProcessQueuedJobAsync(RouteJobRequestedEvent @event, CancellationToken cancellationToken = default)
    {
        var existing = await GetResultAsync(@event.JobId, cancellationToken);
        if (existing?.Status == RouteJobStatus.Completed)
        {
            _jobs[@event.JobId] = existing;
            return;
        }

        await ComputeAsync(
            @event.JobId,
            @event.Kind,
            @event.Request,
            hazards: null,
            @event.SubmittedAtUtc,
            cancellationToken);
    }

    public async Task<RouteJobResult?> GetResultAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (_jobs.TryGetValue(jobId, out var result))
        {
            if (result.Status is RouteJobStatus.Pending or RouteJobStatus.Processing)
            {
                var refreshed = await TryReadPersistedJobAsync(jobId, cancellationToken);
                if (refreshed is not null && refreshed.Status != result.Status)
                {
                    _jobs[jobId] = refreshed;
                    return refreshed;
                }
            }

            return result;
        }

        return await TryReadPersistedJobAsync(jobId, cancellationToken);
    }

    private async Task<RouteJobResult?> TryReadPersistedJobAsync(string jobId, CancellationToken cancellationToken)
    {
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
        RouteJobKind kind,
        RouteRequest request,
        List<HazardReport>? hazards,
        DateTime submittedAt,
        CancellationToken stoppingToken)
    {
        var result = await LoadOrCreateJobResultAsync(jobId, kind, submittedAt, CancellationToken.None);
        result.Kind = kind;
        result.Status = RouteJobStatus.Processing;
        await PersistAsync(result, CancellationToken.None);

        try
        {
            if (kind == RouteJobKind.SafePathOptions)
            {
                var options = await ComputeOptionsAsync(request, hazards, stoppingToken);
                result.Options = options;
                result.Route = options?.Recommended;
                result.Status = options?.Recommended is not null ? RouteJobStatus.Completed : RouteJobStatus.Failed;
                result.Error = options?.Recommended is null ? "No route options found." : null;
            }
            else
            {
                var route = await ComputeRouteAsync(request, hazards, stoppingToken);
                result.Route = route;
                result.Status = route is not null ? RouteJobStatus.Completed : RouteJobStatus.Failed;
                result.Error = route is null ? "No route found." : null;
            }
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
        if (result.Status == RouteJobStatus.Completed)
        {
            await RememberCompletedJobAsync(kind, request, jobId, stoppingToken);
        }

        // Auto-expire completed jobs after 5 minutes.
        _ = CleanupAfterDelay(jobId, JobTtl, stoppingToken);
    }

    private async Task<RouteResponse?> ComputeRouteAsync(
        RouteRequest request,
        List<HazardReport>? hazards,
        CancellationToken stoppingToken)
    {
        var waitTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.JobComputationQueueTimeoutSeconds));

        // Try the coalescing layer first; identical requests share a single computation and one limiter lease.
        return await _coalescing.GetOrComputeAsync(
            request,
            async () =>
            {
                await using var lease = await _routeLimiter.TryAcquireAsync(waitTimeout, stoppingToken)
                    ?? throw new RouteCapacityExceededException();

                await using var scope = _scopeFactory.CreateAsyncScope();
                var scopedHazards = await LoadHazardsAsync(scope.ServiceProvider, request, hazards, stoppingToken);
                var routing = scope.ServiceProvider.GetRequiredService<IRoutingService>();
                return await routing.FindSafePathAsync(request, scopedHazards, stoppingToken);
            });
    }

    private async Task<SafePathOptionsResponse?> ComputeOptionsAsync(
        RouteRequest request,
        List<HazardReport>? hazards,
        CancellationToken stoppingToken)
    {
        var waitTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.JobComputationQueueTimeoutSeconds));
        await using var lease = await _routeLimiter.TryAcquireAsync(waitTimeout, stoppingToken)
            ?? throw new RouteCapacityExceededException();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var scopedHazards = await LoadHazardsAsync(scope.ServiceProvider, request, hazards, stoppingToken);
        var routing = scope.ServiceProvider.GetRequiredService<IRoutingService>();
        var options = await routing.FindSafePathWithVariantsAsync(request, scopedHazards, stoppingToken);

        await CacheOptionsResultAsync(scope.ServiceProvider, request, options, scopedHazards, stoppingToken);
        return options;
    }

    private static async Task<List<HazardReport>> LoadHazardsAsync(
        IServiceProvider serviceProvider,
        RouteRequest request,
        List<HazardReport>? hazards,
        CancellationToken cancellationToken)
    {
        if (hazards is not null)
        {
            return hazards;
        }

        var hazardQueries = serviceProvider.GetRequiredService<IHazardQueryService>();
        return await hazardQueries.LoadHazardsForRouteAsync(request, cancellationToken);
    }

    private async Task CacheOptionsResultAsync(
        IServiceProvider serviceProvider,
        RouteRequest request,
        SafePathOptionsResponse options,
        IEnumerable<HazardReport> hazards,
        CancellationToken cancellationToken)
    {
        try
        {
            var routeGraphStatus = serviceProvider.GetRequiredService<IRouteGraphStatusService>();
            var graphVersion = await routeGraphStatus.GetVersionAsync(cancellationToken);
            var contextFingerprint = $"{RouteRequestFingerprint.HazardContext(hazards)}:graph:{graphVersion}";
            var routeCache = serviceProvider.GetRequiredService<IRouteCacheService>();
            var routeCacheKey = BuildRouteCacheKey(routeCache, request, contextFingerprint);
            await routeCache.SetAsync(routeCacheKey, options.Recommended);

            var optionsCache = serviceProvider.GetRequiredService<IRouteOptionsCacheService>();
            var optionsCacheKey = BuildOptionsCacheKey(optionsCache, request, contextFingerprint);
            await optionsCache.SetAsync(optionsCacheKey, options);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Route options result could not be written to cache.");
        }
    }

    private async Task<RouteJobResult> LoadOrCreateJobResultAsync(
        string jobId,
        RouteJobKind kind,
        DateTime submittedAt,
        CancellationToken cancellationToken)
    {
        if (_jobs.TryGetValue(jobId, out var local))
        {
            return local;
        }

        var persisted = await GetResultAsync(jobId, cancellationToken);
        if (persisted is not null)
        {
            _jobs[jobId] = persisted;
            return persisted;
        }

        var created = new RouteJobResult
        {
            JobId = jobId,
            Kind = kind,
            Status = RouteJobStatus.Pending,
            SubmittedAt = submittedAt
        };
        _jobs[jobId] = created;
        return created;
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

    private async Task RememberCompletedJobAsync(
        RouteJobKind kind,
        RouteRequest request,
        string jobId,
        CancellationToken stoppingToken)
    {
        var dedupeKey = BuildJobDedupeKey(kind, request);
        _completedJobsByDedupeKey[dedupeKey] = jobId;

        if (_redis is { IsConnected: true })
        {
            try
            {
                await _redis.GetDatabase().StringSetAsync(dedupeKey, jobId, JobDedupeTtl);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Completed route job {JobId} could not refresh dedupe key.", jobId);
            }
        }

        _ = CleanupCompletedDedupeAfterDelay(dedupeKey, jobId, JobDedupeTtl, stoppingToken);
    }

    private async Task CleanupCompletedDedupeAfterDelay(
        string dedupeKey,
        string jobId,
        TimeSpan delay,
        CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        if (_completedJobsByDedupeKey.TryGetValue(dedupeKey, out var current)
            && string.Equals(current, jobId, StringComparison.Ordinal))
        {
            _completedJobsByDedupeKey.TryRemove(dedupeKey, out _);
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

    private static string BuildRouteCacheKey(
        IRouteCacheService routeCache,
        RouteRequest request,
        string contextFingerprint) =>
        routeCache.BuildKey(
            request.Start.Y,
            request.Start.X,
            request.End.Y,
            request.End.X,
            request.Profile ?? "standard",
            request.SafetyWeight,
            request.Preferences,
            contextFingerprint);

    private static string BuildOptionsCacheKey(
        IRouteOptionsCacheService optionsCache,
        RouteRequest request,
        string contextFingerprint) =>
        optionsCache.BuildKey(
            request.Start.Y,
            request.Start.X,
            request.End.Y,
            request.End.X,
            request.Profile ?? "standard",
            request.SafetyWeight,
            request.Preferences,
            contextFingerprint);

    private static JsonSerializerOptions CreateJobJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        options.Converters.Add(new CoordinateJsonConverter());
        options.Converters.Add(new GeoJsonConverterFactory());
        return options;
    }
}
