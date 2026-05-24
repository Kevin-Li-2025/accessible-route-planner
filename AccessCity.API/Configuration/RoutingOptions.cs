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
    public bool RouteGraphWarmupEnabled { get; set; }
    public int RouteGraphWarmupDelaySeconds { get; set; } = 15;
    public int RouteGraphWarmupIntervalSeconds { get; set; } = 240;
    public List<RouteGraphWarmupRouteOptions> RouteGraphWarmupRoutes { get; set; } = new();
    public bool RequireRouteGraphForReadiness { get; set; }
    public int RouteGraphCacheTtlSeconds { get; set; } = 300;
    public double RouteGraphShardSizeDegrees { get; set; } = 0.01;
    public bool RouteGraphPrepartitionedShardsEnabled { get; set; }
    public int RouteGraphMaxPrepartitionedShardCount { get; set; } = 64;
    public int RouteGraphMinEdgesPerPrepartitionedShard { get; set; } = 250;
    public bool RouteGraphPackedArtifactsEnabled { get; set; } = true;
    public bool RouteGraphFileArtifactStoreEnabled { get; set; }
    public string RouteGraphFileArtifactDirectory { get; set; } = "/app/route-graph-artifacts";
    public bool RouteGraphFileArtifactWriteThroughEnabled { get; set; } = true;
    public bool RouteGraphFileArtifactManifestEnabled { get; set; } = true;
    public string RouteGraphFileArtifactManifestFileName { get; set; } = "manifest.json";
    public int RouteGraphMaxFileArtifactShardLoadCount { get; set; } = 64;
    public bool RouteGraphFileArtifactWarmupEnabled { get; set; }
    public int RouteGraphFileArtifactWarmupDelaySeconds { get; set; } = 5;
    public int RouteGraphFileArtifactWarmupShardLimit { get; set; } = 64;
    public bool RouteGraphFileArtifactWarmupLargestShardsFirst { get; set; } = true;
    public bool RouteGraphOfflineShardArtifactBuildEnabled { get; set; }
    public int RouteGraphOfflineShardArtifactBuildLimit { get; set; }
    public bool RouteGraphAltPreprocessingEnabled { get; set; } = true;
    public int RouteGraphAltLandmarkCount { get; set; } = 4;
    public int RouteGraphMaxAltPreprocessedNodes { get; set; } = 25_000;
    public bool RouteGraphProfileAndExit { get; set; }
    public bool RouteGraphProfileUseOsmExtract { get; set; } = true;
    public bool RouteGraphDistributedLoadCoalescingEnabled { get; set; } = true;
    public int RouteGraphDistributedLoadLockTtlSeconds { get; set; } = 8;
    public int RouteGraphDistributedLoadWaitMilliseconds { get; set; } = 3_500;
    public bool DistributedCoalescingEnabled { get; set; } = true;
    public int DistributedCoalescingLockTtlSeconds { get; set; } = 6;
    public int DistributedCoalescingResultTtlSeconds { get; set; } = 10;
    public int DistributedCoalescingWaitMilliseconds { get; set; } = 3_500;
    public int DistributedCoalescingPollMilliseconds { get; set; } = 25;
}

public sealed class RouteGraphWarmupRouteOptions
{
    public string Name { get; set; } = "route";
    public double StartLat { get; set; }
    public double StartLng { get; set; }
    public double EndLat { get; set; }
    public double EndLng { get; set; }
}
