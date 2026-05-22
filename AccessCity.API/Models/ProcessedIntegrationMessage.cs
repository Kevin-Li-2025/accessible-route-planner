namespace AccessCity.API.Models;

public sealed class ProcessedIntegrationMessage
{
    public long Id { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string ConsumerGroupId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime ProcessedAtUtc { get; set; }
}
