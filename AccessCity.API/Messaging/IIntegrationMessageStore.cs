using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AccessCity.API.Messaging;

public interface IIntegrationMessageStore
{
    Task<bool> HasProcessedAsync(string messageId, string topic, string consumerGroupId, CancellationToken cancellationToken);
    Task MarkProcessedAsync(string messageId, string topic, string consumerGroupId, string eventType, CancellationToken cancellationToken);
}

public sealed class EfIntegrationMessageStore : IIntegrationMessageStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfIntegrationMessageStore> _logger;

    public EfIntegrationMessageStore(IServiceScopeFactory scopeFactory, ILogger<EfIntegrationMessageStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<bool> HasProcessedAsync(
        string messageId,
        string topic,
        string consumerGroupId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (IsInMemory(db))
        {
            return false;
        }

        return await db.ProcessedIntegrationMessages.AsNoTracking().AnyAsync(
            message => message.MessageId == messageId
                && message.ConsumerGroupId == consumerGroupId,
            cancellationToken);
    }

    public async Task MarkProcessedAsync(
        string messageId,
        string topic,
        string consumerGroupId,
        string eventType,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (IsInMemory(db))
        {
            return;
        }

        db.ProcessedIntegrationMessages.Add(new ProcessedIntegrationMessage
        {
            MessageId = messageId,
            Topic = topic,
            ConsumerGroupId = consumerGroupId,
            EventType = eventType,
            ProcessedAtUtc = DateTime.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogInformation(
                ex,
                "Processed-message marker already exists for {MessageId} on {Topic}/{ConsumerGroupId}",
                messageId,
                topic,
                consumerGroupId);
        }
    }

    private static bool IsInMemory(AppDbContext db) =>
        string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);
}
