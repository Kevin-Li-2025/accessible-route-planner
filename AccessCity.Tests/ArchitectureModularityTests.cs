using AccessCity.API.Controllers;
using AccessCity.API.Data;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace AccessCity.Tests;

public sealed class ArchitectureModularityTests
{
    [Fact]
    public void Controllers_do_not_inject_app_db_context_directly()
    {
        var controllerTypes = typeof(RoutingController).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "AccessCity.API.Controllers"
                           && !type.IsAbstract
                           && typeof(ControllerBase).IsAssignableFrom(type))
            .ToList();

        var violations = controllerTypes
            .SelectMany(type => type.GetConstructors()
                .SelectMany(ctor => ctor.GetParameters()
                    .Where(parameter => parameter.ParameterType == typeof(AppDbContext))
                    .Select(parameter => $"{type.Name}.{ctor.Name}({parameter.Name}: AppDbContext)")))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Controllers_do_not_reference_data_namespace_in_source()
    {
        var root = FindRepositoryRoot();
        var controllerDirectory = Path.Combine(root, "AccessCity.API", "Controllers");
        var violations = Directory.EnumerateFiles(controllerDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { file, line, index })
                .Where(item => item.line.Contains("AccessCity.API.Data", StringComparison.Ordinal)
                               || item.line.Contains("AppDbContext", StringComparison.Ordinal))
                .Select(item => $"{Path.GetFileName(item.file)}:{item.index + 1}: {item.line.Trim()}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Controllers_depend_on_application_service_interfaces()
    {
        var controllerTypes = typeof(RoutingController).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "AccessCity.API.Controllers"
                           && !type.IsAbstract
                           && typeof(ControllerBase).IsAssignableFrom(type))
            .ToList();

        var violations = controllerTypes
            .SelectMany(type => type.GetConstructors()
                .SelectMany(ctor => ctor.GetParameters()
                    .Where(parameter => IsConcreteAccessCityService(parameter.ParameterType))
                    .Select(parameter => $"{type.Name}.{ctor.Name}({parameter.Name}: {parameter.ParameterType.Name})")))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Services_and_modules_do_not_reference_controllers()
    {
        var root = FindRepositoryRoot();
        var sourceDirectories = new[]
        {
            Path.Combine(root, "AccessCity.API", "Services"),
            Path.Combine(root, "AccessCity.API", "Modules")
        };

        var violations = sourceDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { file, line, index })
                .Where(item => item.line.Contains("AccessCity.API.Controllers", StringComparison.Ordinal))
                .Select(item => $"{Path.GetRelativePath(root, item.file)}:{item.index + 1}: {item.line.Trim()}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Kubernetes_uses_one_api_autoscaler_with_latency_signal()
    {
        var root = FindRepositoryRoot();
        var kustomization = File.ReadAllText(Path.Combine(root, "deploy", "kubernetes", "kustomization.yaml"));
        var scaledObject = File.ReadAllText(Path.Combine(root, "deploy", "kubernetes", "keda-scaledobject.yaml"));

        Assert.DoesNotContain("hpa.yaml", kustomization, StringComparison.Ordinal);
        Assert.Contains("name: accesscity-api-scalability", scaledObject, StringComparison.Ordinal);
        Assert.Contains("accesscity_safe_path_p95_ms", scaledObject, StringComparison.Ordinal);
        Assert.Contains("accesscity_route_capacity_saturation_rate", scaledObject, StringComparison.Ordinal);
        Assert.Contains("accesscity_api_cpu_request_utilization", scaledObject, StringComparison.Ordinal);
        Assert.Contains("accesscity_api_memory_limit_utilization", scaledObject, StringComparison.Ordinal);
        Assert.Contains("fallback:", scaledObject, StringComparison.Ordinal);
        Assert.Contains("failureThreshold: 3", scaledObject, StringComparison.Ordinal);
        Assert.Contains("type: cron", scaledObject, StringComparison.Ordinal);
        Assert.Contains("desiredReplicas: \"10\"", scaledObject, StringComparison.Ordinal);
        Assert.DoesNotContain("type: cpu", scaledObject, StringComparison.Ordinal);
        Assert.DoesNotContain("type: memory", scaledObject, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_config_keeps_external_latency_off_request_hot_paths()
    {
        var root = FindRepositoryRoot();
        var configMap = File.ReadAllText(Path.Combine(root, "deploy", "kubernetes", "configmap.yaml"));

        Assert.Contains("Routing__AsyncFirstForCacheMiss: \"true\"", configMap, StringComparison.Ordinal);
        Assert.Contains("RiskScoring__RealtimeExternalSignalsEnabled: \"false\"", configMap, StringComparison.Ordinal);
        Assert.Contains("ExternalApis__Overpass__RealtimeHazardsEnabled: \"false\"", configMap, StringComparison.Ordinal);
        Assert.Contains("ExternalApis__Overpass__HazardFetchBudgetSeconds: \"5\"", configMap, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_jobs_publish_status_to_distributed_cache_for_multi_replica_polling()
    {
        var root = FindRepositoryRoot();
        var routeJobService = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RouteJobService.cs"));

        Assert.Contains("IDistributedCache", routeJobService, StringComparison.Ordinal);
        Assert.Contains("SetStringAsync", routeJobService, StringComparison.Ordinal);
        Assert.Contains("GetStringAsync", routeJobService, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_jobs_are_dispatched_to_worker_path_in_production()
    {
        var root = FindRepositoryRoot();
        var configMap = File.ReadAllText(Path.Combine(root, "deploy", "kubernetes", "configmap.yaml"));
        var routingModule = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Modules", "RoutingModule.cs"));
        var routeJobService = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RouteJobService.cs"));

        Assert.Contains("RouteJobBackgroundService", routingModule, StringComparison.Ordinal);
        Assert.Contains("RouteJobRequestedEvent", routeJobService, StringComparison.Ordinal);
        Assert.Contains("Routing__DispatchJobsToWorker: \"true\"", configMap, StringComparison.Ordinal);
        Assert.Contains("Workers__Routing__Enabled: \"false\"", configMap, StringComparison.Ordinal);
        Assert.Contains("Workers__Routing__Enabled: \"true\"", configMap, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_graph_repository_caches_preindexed_shards()
    {
        var root = FindRepositoryRoot();
        var repository = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RouteGraphRepository.cs"));
        var models = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Models", "RouteGraphModels.cs"));
        var routing = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RoutingService.cs"));

        Assert.Contains("IMemoryCache", repository, StringComparison.Ordinal);
        Assert.Contains("IDistributedCache", repository, StringComparison.Ordinal);
        Assert.Contains("InFlightGraphLoads", repository, StringComparison.Ordinal);
        Assert.Contains("RouteGraphSnapshot", repository, StringComparison.Ordinal);
        Assert.Contains("ComputeShardRegion", repository, StringComparison.Ordinal);
        Assert.Contains("BuildSpatialBuckets", repository, StringComparison.Ordinal);
        Assert.Contains("SpatialBuckets", models, StringComparison.Ordinal);
        Assert.Contains("FindNodesNear", routing, StringComparison.Ordinal);
    }

    [Fact]
    public void Kafka_processed_message_identity_includes_topic()
    {
        var root = FindRepositoryRoot();
        var kafkaBus = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Messaging", "Kafka", "KafkaMessageBus.cs"));

        Assert.Contains("BuildMessageIdentity", kafkaBus, StringComparison.Ordinal);
        Assert.Contains("return $\"{canonicalTopic}:{rawId}\";", kafkaBus, StringComparison.Ordinal);
    }

    [Fact]
    public void Distributed_startup_paths_avoid_schema_and_topic_races()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Program.cs"));
        var startup = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Extensions", "WebApplicationExtensions.cs"));
        var kafkaBus = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Messaging", "Kafka", "KafkaMessageBus.cs"));
        var migrationJob = File.ReadAllText(Path.Combine(root, "deploy", "kubernetes", "migration-job.yaml"));
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));

        Assert.Contains("Postgres:MigrateAndExit", program, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_lock", startup, StringComparison.Ordinal);
        Assert.Contains("EnsureTopicsAsync", kafkaBus, StringComparison.Ordinal);
        Assert.Contains("Postgres__MigrateAndExit", migrationJob, StringComparison.Ordinal);
        Assert.Contains("profiles: [\"migrate\"]", compose, StringComparison.Ordinal);
        Assert.Contains("apache/kafka", compose, StringComparison.Ordinal);
    }

    private static bool IsConcreteAccessCityService(Type type)
    {
        if (type == typeof(AccessCityMetrics))
        {
            return false;
        }

        return type.IsClass
               && type.Namespace?.StartsWith("AccessCity.API.Services", StringComparison.Ordinal) == true;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CodeConquerors.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing CodeConquerors.sln.");
    }
}
