using AccessCity.API.Configuration;
using AccessCity.API.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AccessCity.API.HealthChecks;

public sealed class RouteGraphArtifactManifestHealthCheck : IHealthCheck
{
    private readonly IRouteGraphArtifactStore _artifactStore;
    private readonly RoutingOptions _options;

    public RouteGraphArtifactManifestHealthCheck(
        IRouteGraphArtifactStore artifactStore,
        IOptions<RoutingOptions> options)
    {
        _artifactStore = artifactStore;
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["fileArtifactStoreEnabled"] = _artifactStore.IsEnabled,
            ["manifestEnabled"] = _options.RouteGraphFileArtifactManifestEnabled
        };

        if (!_artifactStore.IsEnabled || !_options.RouteGraphFileArtifactManifestEnabled)
        {
            return HealthCheckResult.Healthy("Route graph artifact manifest is disabled.", data);
        }

        var manifest = await _artifactStore.TryReadManifestAsync(cancellationToken);
        if (manifest is null || manifest.Shards.Length == 0)
        {
            data["shardCount"] = 0;
            const string message = "Route graph artifact manifest is missing, empty, or incompatible.";
            return _options.RequireRouteGraphForReadiness
                ? HealthCheckResult.Unhealthy(message, data: data)
                : HealthCheckResult.Degraded(message, data: data);
        }

        data["schemaVersion"] = manifest.SchemaVersion;
        data["edgeCostVersion"] = manifest.EdgeCostVersion;
        data["edgeWeightVersion"] = manifest.EdgeWeightVersion;
        data["altAlgorithmVersion"] = manifest.AltAlgorithmVersion;
        data["sourceName"] = manifest.SourceName;
        data["shardCount"] = manifest.Shards.Length;
        data["totalPayloadBytes"] = manifest.Shards.Sum(shard => shard.PayloadBytes);

        return HealthCheckResult.Healthy("Route graph artifact manifest is available.", data);
    }
}
