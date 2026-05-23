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
    public void Readiness_probe_uses_background_refreshed_snapshot()
    {
        var root = FindRepositoryRoot();
        var dependencyInjection = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Extensions", "DependencyInjection.cs"));
        var cachedReadiness = File.ReadAllText(Path.Combine(root, "AccessCity.API", "HealthChecks", "CachedReadinessService.cs"));
        var warmupService = File.ReadAllText(Path.Combine(root, "AccessCity.API", "HealthChecks", "ReadinessWarmupBackgroundService.cs"));
        var configMap = File.ReadAllText(Path.Combine(root, "deploy", "kubernetes", "configmap.yaml"));

        Assert.Contains("AddHostedService<ReadinessWarmupBackgroundService>", dependencyInjection, StringComparison.Ordinal);
        Assert.Contains("QueueBackgroundRefresh", cachedReadiness, StringComparison.Ordinal);
        Assert.Contains("return cached.Report", cachedReadiness, StringComparison.Ordinal);
        Assert.Contains("ReadinessBackgroundRefreshMilliseconds", warmupService, StringComparison.Ordinal);
        Assert.Contains("HealthChecks__ReadinessBackgroundRefreshMilliseconds: \"2000\"", configMap, StringComparison.Ordinal);
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
        var scaledObject = File.ReadAllText(Path.Combine(root, "deploy", "kubernetes", "keda-scaledobject.yaml"));
        var routingModule = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Modules", "RoutingModule.cs"));
        var routeJobService = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RouteJobService.cs"));
        var kafkaBus = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Messaging", "Kafka", "KafkaMessageBus.cs"));
        var kafkaWarmup = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Messaging", "Kafka", "KafkaTopicWarmupBackgroundService.cs"));
        var dependencyInjection = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Extensions", "DependencyInjection.cs"));
        var routingController = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Controllers", "RoutingController.cs"));

        Assert.Contains("RouteJobBackgroundService", routingModule, StringComparison.Ordinal);
        Assert.Contains("RouteJobDispatchBackgroundService", routingModule, StringComparison.Ordinal);
        Assert.Contains("Channel<RouteJobDispatchWorkItem>", routeJobService, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", routeJobService, StringComparison.Ordinal);
        Assert.Contains("RouteJobRequestedEvent", routeJobService, StringComparison.Ordinal);
        Assert.Contains("Routing__DispatchJobsToWorker: \"true\"", configMap, StringComparison.Ordinal);
        Assert.Contains("Messaging__UseKafka: ${MESSAGING_USE_KAFKA:-true}", File.ReadAllText(Path.Combine(root, "docker-compose.yml")), StringComparison.Ordinal);
        Assert.Contains("Workers__Routing__Enabled: \"false\"", configMap, StringComparison.Ordinal);
        Assert.Contains("Workers__Routing__Enabled: \"true\"", configMap, StringComparison.Ordinal);
        Assert.Contains("Kafka__TopicPartitions: \"48\"", configMap, StringComparison.Ordinal);
        Assert.Contains("topic: accesscity_routejobrequestedevent", scaledObject, StringComparison.Ordinal);
        Assert.Contains("minReplicaCount: 6", scaledObject, StringComparison.Ordinal);
        Assert.Contains("maxReplicaCount: 100", scaledObject, StringComparison.Ordinal);
        Assert.Contains("CreatePartitionsAsync", kafkaBus, StringComparison.Ordinal);
        Assert.Contains("SafePathOptionsResponse? Options", routeJobService, StringComparison.Ordinal);
        Assert.Contains("SubmitOptionsAsync", routeJobService, StringComparison.Ordinal);
        Assert.Contains("SubmitOptionsAsync", routingController, StringComparison.Ordinal);
        Assert.Contains("completed_job_hit", routingController, StringComparison.Ordinal);
        Assert.Contains("_completedJobsByDedupeKey", routeJobService, StringComparison.Ordinal);
        Assert.Contains("TryReadPersistedJobAsync", routeJobService, StringComparison.Ordinal);
        Assert.Contains("TryRecoverPendingJobDispatchAsync", routeJobService, StringComparison.Ordinal);
        Assert.Contains("route_job:redispatch", routeJobService, StringComparison.Ordinal);
        Assert.Contains("PublishRouteJobRequestAsync", routeJobService, StringComparison.Ordinal);
        Assert.Contains("directly re-published", routeJobService, StringComparison.Ordinal);
        Assert.Contains("IKafkaTopicInitializer", kafkaBus, StringComparison.Ordinal);
        Assert.Contains("EnsureInfrastructureAsync", kafkaBus, StringComparison.Ordinal);
        Assert.Contains("EnsureTopicBeforePublishAsync", kafkaBus, StringComparison.Ordinal);
        Assert.Contains("publishing optimistically", kafkaBus, StringComparison.Ordinal);
        Assert.Contains("TopicAdminTimeoutSeconds", kafkaBus, StringComparison.Ordinal);
        Assert.Contains("KafkaTopicWarmupBackgroundService", kafkaWarmup, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(timeoutCts.Token)", kafkaWarmup, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<KafkaTopicWarmupBackgroundService>", dependencyInjection, StringComparison.Ordinal);
        Assert.Contains("Kafka__TopicWarmupTimeoutSeconds: \"10\"", configMap, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_graph_repository_caches_preindexed_shards()
    {
        var root = FindRepositoryRoot();
        var repository = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RouteGraphRepository.cs"));
        var artifactCodec = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RouteGraphArtifactCodec.cs"));
        var models = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Models", "RouteGraphModels.cs"));
        var routing = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RoutingService.cs"));
        var configMap = File.ReadAllText(Path.Combine(root, "deploy", "kubernetes", "configmap.yaml"));

        Assert.Contains("IMemoryCache", repository, StringComparison.Ordinal);
        Assert.Contains("IDistributedCache", repository, StringComparison.Ordinal);
        Assert.Contains("InFlightGraphLoads", repository, StringComparison.Ordinal);
        Assert.Contains("PackedRouteGraphArtifact", artifactCodec, StringComparison.Ordinal);
        Assert.Contains("FirstEdgeIndex", artifactCodec, StringComparison.Ordinal);
        Assert.Contains("ComputeShardRegion", repository, StringComparison.Ordinal);
        Assert.Contains("ComputeLoadRegions", repository, StringComparison.Ordinal);
        Assert.Contains("MergeGraphShards", repository, StringComparison.Ordinal);
        Assert.Contains("route_graph:v6", repository, StringComparison.Ordinal);
        Assert.Contains("Routing__RouteGraphPrepartitionedShardsEnabled: \"true\"", configMap, StringComparison.Ordinal);
        Assert.Contains("Routing__RouteGraphPackedArtifactsEnabled: \"true\"", configMap, StringComparison.Ordinal);
        Assert.Contains("BuildSpatialBuckets", repository, StringComparison.Ordinal);
        Assert.Contains("SpatialBuckets", models, StringComparison.Ordinal);
        Assert.Contains("FindNodesNear", routing, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_edge_costs_are_precomputed_and_versioned_for_worker_routing()
    {
        var root = FindRepositoryRoot();
        var costModel = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RouteEdgeCostModel.cs"));
        var importer = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "OsmImportService.cs"));
        var routing = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RoutingService.cs"));
        var routeGraph = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RouteGraphRepository.cs"));
        var fingerprint = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RouteRequestFingerprint.cs"));
        var migration = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Data", "Migrations", "20260523223500_AddRouteEdgeAccessibilityCostProfile.cs"));

        Assert.Contains("public const int Version = 1", costModel, StringComparison.Ordinal);
        Assert.Contains("public const int EdgeWeightVersion = 1", costModel, StringComparison.Ordinal);
        Assert.Contains("RouteEdgeCostModel.Compute", importer, StringComparison.Ordinal);
        Assert.Contains("RouteEdgeCostModel.ResolveTraversalSeconds", routing, StringComparison.Ordinal);
        Assert.Contains("WheelchairAccessibilityPenaltySeconds", routeGraph, StringComparison.Ordinal);
        Assert.Contains("RouteEdgeCostModel.EdgeWeightVersion", routeGraph, StringComparison.Ordinal);
        Assert.Contains("route-v6-packed-graph-edge-weight-v1-risk-v2", fingerprint, StringComparison.Ordinal);
        Assert.Contains("wheelchair_accessibility_penalty_seconds", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_graph_hot_reads_can_use_read_only_postgres_path()
    {
        var root = FindRepositoryRoot();
        var factory = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Data", "HotPathDbContextFactory.cs"));
        var resolver = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Configuration", "PostgresConnectionStringResolver.cs"));
        var repository = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RouteGraphRepository.cs"));
        var configMap = File.ReadAllText(Path.Combine(root, "deploy", "kubernetes", "configmap.yaml"));

        Assert.Contains("IHotPathDbContextFactory", factory, StringComparison.Ordinal);
        Assert.Contains("READONLY_DATABASE_URL", resolver, StringComparison.Ordinal);
        Assert.Contains("READ_REPLICA_DATABASE_URL", resolver, StringComparison.Ordinal);
        Assert.Contains("CreateDbContext()", repository, StringComparison.Ordinal);
        Assert.Contains("Postgres__UseReadOnlyForHotPaths: \"true\"", configMap, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_graph_warmup_is_worker_scoped_and_uses_configured_shards()
    {
        var root = FindRepositoryRoot();
        var routingModule = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Modules", "RoutingModule.cs"));
        var warmupService = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "Background", "RouteGraphWarmupBackgroundService.cs"));
        var configMap = File.ReadAllText(Path.Combine(root, "deploy", "kubernetes", "configmap.yaml"));

        Assert.Contains("RouteGraphWarmupBackgroundService", routingModule, StringComparison.Ordinal);
        Assert.Contains("LoadGraphAsync", warmupService, StringComparison.Ordinal);
        Assert.Contains("Routing__RouteGraphWarmupEnabled: \"false\"", configMap, StringComparison.Ordinal);
        Assert.Contains("Routing__RouteGraphWarmupEnabled: \"true\"", configMap, StringComparison.Ordinal);
        Assert.Contains("Routing__RouteGraphWarmupRoutes__0__Name: \"birmingham-core\"", configMap, StringComparison.Ordinal);
        Assert.Contains("Routing__RouteGraphWarmupRoutes__3__Name: \"birmingham-jewellery-core\"", configMap, StringComparison.Ordinal);
    }

    [Fact]
    public void Accessibility_routing_penalizes_and_explains_incomplete_osm_tags()
    {
        var root = FindRepositoryRoot();
        var routing = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RoutingService.cs"));

        Assert.Contains("ComputeAccessibilityDataGapPenalty", routing, StringComparison.Ordinal);
        Assert.Contains("BuildAccessibilityDataQualitySummary", routing, StringComparison.Ordinal);
        Assert.Contains("Accessibility data confidence is lower", routing, StringComparison.Ordinal);
        Assert.Contains("missing width", routing, StringComparison.Ordinal);
        Assert.Contains("missing smoothness", routing, StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_assist_does_not_enter_route_decision_path()
    {
        var root = FindRepositoryRoot();
        var routing = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "RoutingService.cs"));
        var aiAssist = File.ReadAllText(Path.Combine(root, "AccessCity.API", "Services", "AiAssistService.cs"));

        Assert.DoesNotContain("IAiAssistService", routing, StringComparison.Ordinal);
        Assert.DoesNotContain("AiAssistService", routing, StringComparison.Ordinal);
        Assert.Contains("ForRouteDecision = false", aiAssist, StringComparison.Ordinal);
        Assert.Contains("CanAutoApply = false", aiAssist, StringComparison.Ordinal);
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
