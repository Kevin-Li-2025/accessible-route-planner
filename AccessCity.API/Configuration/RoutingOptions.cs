namespace AccessCity.API.Configuration;

public sealed class RoutingOptions
{
    public const string SectionName = "Routing";

    public int MaxConcurrentComputations { get; set; } = 4;
    public int SyncSafePathTimeoutSeconds { get; set; } = 4;
    public int ComputationQueueTimeoutSeconds { get; set; } = 2;
    public int JobComputationQueueTimeoutSeconds { get; set; } = 30;
    public int HazardQueryPaddingMetres { get; set; } = 250;
    public int MaxHazardsPerRequest { get; set; } = 500;
    public int MaxRiskQueryRadiusMetres { get; set; } = 2_500;
    public int MaxRouteGraphEdges { get; set; } = 20_000;
    public bool AsyncFirstForCacheMiss { get; set; }
}
