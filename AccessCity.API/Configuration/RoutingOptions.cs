namespace AccessCity.API.Configuration;

public sealed class RoutingOptions
{
    public const string SectionName = "Routing";

    public int MaxConcurrentComputations { get; set; } = 4;
    public int SyncSafePathTimeoutSeconds { get; set; } = 4;
    public int ComputationQueueTimeoutSeconds { get; set; } = 2;
    public int JobComputationQueueTimeoutSeconds { get; set; } = 30;
    public int RouteJobDispatchConcurrency { get; set; } = 2;
    public int HazardQueryPaddingMetres { get; set; } = 250;
    public int MaxHazardsPerRequest { get; set; } = 500;
    public int MaxRiskQueryRadiusMetres { get; set; } = 2_500;
    public int MaxRouteGraphEdges { get; set; } = 20_000;
    public bool AsyncFirstForCacheMiss { get; set; }
    public int AsyncFirstCacheProbeMilliseconds { get; set; } = 150;
    public bool DispatchJobsToWorker { get; set; }
    public bool RequireRouteGraphForReadiness { get; set; }
    public int RouteGraphCacheTtlSeconds { get; set; } = 300;
    public double RouteGraphShardSizeDegrees { get; set; } = 0.01;
    public bool RouteGraphDistributedLoadCoalescingEnabled { get; set; } = true;
    public int RouteGraphDistributedLoadLockTtlSeconds { get; set; } = 8;
    public int RouteGraphDistributedLoadWaitMilliseconds { get; set; } = 3_500;
    public bool DistributedCoalescingEnabled { get; set; } = true;
    public int DistributedCoalescingLockTtlSeconds { get; set; } = 6;
    public int DistributedCoalescingResultTtlSeconds { get; set; } = 10;
    public int DistributedCoalescingWaitMilliseconds { get; set; } = 3_500;
    public int DistributedCoalescingPollMilliseconds { get; set; } = 25;
}
