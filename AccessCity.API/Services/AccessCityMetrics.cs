using System.Diagnostics.Metrics;

namespace AccessCity.API.Services;

public sealed class AccessCityMetrics
{
    public const string MeterName = "AccessCity.API";
    private static readonly Meter Meter = new(MeterName);

    private readonly Counter<long> _kafkaProcessed;
    private readonly Counter<long> _kafkaFailed;
    private readonly Counter<long> _kafkaDlq;
    private readonly Histogram<double> _osmImportDuration;
    private readonly Histogram<double> _cacheLatency;
    private readonly Counter<long> _cacheHit;
    private readonly Counter<long> _cacheMiss;
    private readonly Histogram<double> _safePathDuration;
    private readonly Histogram<double> _routeComputationQueueDuration;
    private readonly Counter<long> _routeComputationSaturated;
    private readonly UpDownCounter<long> _routeComputationInflight;
    private readonly Counter<long> _routeCoalescing;
    private readonly Histogram<double> _routeGraphLoadDuration;
    private readonly Histogram<long> _routeGraphLoadEdges;
    private readonly Counter<long> _routeGraphLoad;
    private readonly Histogram<double> _externalDependencyDuration;
    private readonly Counter<long> _externalDependencyFallback;
    private readonly Counter<long> _externalDependencyCircuitOpened;

    public AccessCityMetrics()
    {
        _kafkaProcessed = Meter.CreateCounter<long>("accesscity.kafka.messages.processed");
        _kafkaFailed = Meter.CreateCounter<long>("accesscity.kafka.messages.failed");
        _kafkaDlq = Meter.CreateCounter<long>("accesscity.kafka.messages.dead_lettered");
        _osmImportDuration = Meter.CreateHistogram<double>("accesscity.osm_import.duration", "ms");
        _cacheLatency = Meter.CreateHistogram<double>("accesscity.cache.lookup.duration", "ms");
        _cacheHit = Meter.CreateCounter<long>("accesscity.cache.hit");
        _cacheMiss = Meter.CreateCounter<long>("accesscity.cache.miss");
        _safePathDuration = Meter.CreateHistogram<double>("accesscity.route.safe_path.duration", "ms");
        _routeComputationQueueDuration = Meter.CreateHistogram<double>("accesscity.route.computation.queue.duration", "ms");
        _routeComputationSaturated = Meter.CreateCounter<long>("accesscity.route.computation.saturated");
        _routeComputationInflight = Meter.CreateUpDownCounter<long>("accesscity.route.computation.inflight");
        _routeCoalescing = Meter.CreateCounter<long>("accesscity.route.coalescing");
        _routeGraphLoadDuration = Meter.CreateHistogram<double>("accesscity.route_graph.load.duration", "ms");
        _routeGraphLoadEdges = Meter.CreateHistogram<long>("accesscity.route_graph.load.edges", "edges");
        _routeGraphLoad = Meter.CreateCounter<long>("accesscity.route_graph.load.total");
        _externalDependencyDuration = Meter.CreateHistogram<double>("accesscity.external_dependency.duration", "ms");
        _externalDependencyFallback = Meter.CreateCounter<long>("accesscity.external_dependency.fallback");
        _externalDependencyCircuitOpened = Meter.CreateCounter<long>("accesscity.external_dependency.circuit_opened");
    }

    public void KafkaProcessed(string topic) =>
        _kafkaProcessed.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", topic));

    public void KafkaFailed(string topic) =>
        _kafkaFailed.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", topic));

    public void KafkaDeadLettered(string topic) =>
        _kafkaDlq.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", topic));

    public void OsmImportCompleted(double milliseconds, string status) =>
        _osmImportDuration.Record(milliseconds, new KeyValuePair<string, object?>("job.status", status));

    public void CacheLookup(string cacheName, bool hit, double milliseconds)
    {
        _cacheLatency.Record(milliseconds, new KeyValuePair<string, object?>("cache.name", cacheName));
        if (hit)
        {
            _cacheHit.Add(1, new KeyValuePair<string, object?>("cache.name", cacheName));
        }
        else
        {
            _cacheMiss.Add(1, new KeyValuePair<string, object?>("cache.name", cacheName));
        }
    }

    public void SafePathCompleted(string route, string outcome, double milliseconds) =>
        _safePathDuration.Record(
            milliseconds,
            new KeyValuePair<string, object?>("http.route", route),
            new KeyValuePair<string, object?>("route.outcome", outcome));

    public void RouteComputationQueueWait(string outcome, double milliseconds) =>
        _routeComputationQueueDuration.Record(
            milliseconds,
            new KeyValuePair<string, object?>("route.queue.outcome", outcome));

    public void RouteComputationSaturated() => _routeComputationSaturated.Add(1);

    public void RouteComputationStarted() => _routeComputationInflight.Add(1);

    public void RouteComputationCompleted() => _routeComputationInflight.Add(-1);

    public void RouteCoalescing(string outcome) =>
        _routeCoalescing.Add(1, new KeyValuePair<string, object?>("route.coalescing.outcome", outcome));

    public void RouteGraphLoad(string source, string outcome, double milliseconds, int edgeCount)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>("route_graph.source", source),
            new KeyValuePair<string, object?>("route_graph.outcome", outcome)
        };
        _routeGraphLoadDuration.Record(milliseconds, tags);
        _routeGraphLoadEdges.Record(edgeCount, tags);
        _routeGraphLoad.Add(1, tags);
    }

    public void ExternalDependencyCompleted(string dependencyName, string outcome, double milliseconds) =>
        _externalDependencyDuration.Record(
            milliseconds,
            new KeyValuePair<string, object?>("dependency.name", dependencyName),
            new KeyValuePair<string, object?>("dependency.outcome", outcome));

    public void ExternalDependencyFallback(string dependencyName, string reason) =>
        _externalDependencyFallback.Add(
            1,
            new KeyValuePair<string, object?>("dependency.name", dependencyName),
            new KeyValuePair<string, object?>("dependency.fallback.reason", reason));

    public void ExternalDependencyCircuitOpened(string dependencyName) =>
        _externalDependencyCircuitOpened.Add(1, new KeyValuePair<string, object?>("dependency.name", dependencyName));
}
