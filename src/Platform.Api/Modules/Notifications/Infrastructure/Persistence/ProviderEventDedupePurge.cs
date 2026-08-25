using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// One purge round: removes every provider deduplication mark older than the
/// configured retention. Age-based and idempotent, so overlapping or retried
/// rounds never remove a mark that could still refuse a redelivery. The
/// evidence itself is untouched: only the identity ledger shrinks.
/// </summary>
internal sealed class ProviderEventDedupePurge(
    NotificationsDbContext db,
    IOptions<ProviderEventDedupePurgeOptions> options,
    TimeProvider timeProvider,
    ILogger<ProviderEventDedupePurge> logger)
{
    /// <summary>Runs one round and returns how many marks were removed.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset threshold = timeProvider.GetUtcNow() - options.Value.Retention;
        var removed = await db.ProviderEventDedupes
            .Where(mark => mark.ProcessedAt < threshold)
            .ExecuteDeleteAsync(cancellationToken);
        if (removed > 0) logger.ProviderEventDedupePurged(removed, threshold);

        return removed;
    }
}
