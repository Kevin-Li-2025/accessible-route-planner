using AccessCity.API.Messaging.Kafka;
using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AccessCity.API.HealthChecks;

public sealed class KafkaHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;
    private readonly IOptions<KafkaOptions> _options;

    public KafkaHealthCheck(IConfiguration configuration, IOptions<KafkaOptions> options)
    {
        _configuration = configuration;
        _options = options;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_configuration.GetValue<bool>("Messaging:UseKafka"))
        {
            return Task.FromResult(HealthCheckResult.Healthy("Kafka disabled."));
        }

        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = _options.Value.BootstrapServers,
                ClientId = "AccessCity.API.health"
            }).Build();

            var metadata = admin.GetMetadata(TimeSpan.FromSeconds(2));
            return metadata.Brokers.Count > 0
                ? Task.FromResult(HealthCheckResult.Healthy())
                : Task.FromResult(HealthCheckResult.Unhealthy("Kafka returned no brokers."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Kafka is not reachable.", ex));
        }
    }
}
