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

    public AccessCityMetrics()
    {
        _kafkaProcessed = Meter.CreateCounter<long>("accesscity.kafka.messages.processed");
        _kafkaFailed = Meter.CreateCounter<long>("accesscity.kafka.messages.failed");
        _kafkaDlq = Meter.CreateCounter<long>("accesscity.kafka.messages.dead_lettered");
        _osmImportDuration = Meter.CreateHistogram<double>("accesscity.osm_import.duration", "ms");
        _cacheLatency = Meter.CreateHistogram<double>("accesscity.cache.lookup.duration", "ms");
        _cacheHit = Meter.CreateCounter<long>("accesscity.cache.hit");
        _cacheMiss = Meter.CreateCounter<long>("accesscity.cache.miss");
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
}
