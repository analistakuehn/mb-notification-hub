using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// One purge round: removes every processed mark older than the configured
/// retention. Age-based and idempotent, so overlapping or retried rounds
/// never remove a mark that could still dedupe a redelivery.
/// </summary>
internal sealed class ProcessedMessagePurge(
    PlatformMessagingDbContext db,
    IOptions<ProcessedMessagePurgeOptions> options,
    TimeProvider timeProvider,
    ILogger<ProcessedMessagePurge> logger)
{
    /// <summary>Runs one round and returns how many marks were removed.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset threshold = timeProvider.GetUtcNow() - options.Value.Retention;
        var removed = await db.ProcessedMessages
            .Where(message => message.ProcessedAt < threshold)
            .ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
        {
            logger.ProcessedMessagesPurged(removed, threshold);
        }

        return removed;
    }
}
