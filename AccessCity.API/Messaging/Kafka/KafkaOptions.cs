namespace AccessCity.API.Messaging.Kafka;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";
    public string ConsumerGroupId { get; set; } = "accesscity-workers";
    public string TopicPrefix { get; set; } = "accesscity_";
    public int MaxProcessingAttempts { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 5;
    public string RetryTopicSuffix { get; set; } = ".retry";
    public string DeadLetterTopicSuffix { get; set; } = ".dlq";
}
