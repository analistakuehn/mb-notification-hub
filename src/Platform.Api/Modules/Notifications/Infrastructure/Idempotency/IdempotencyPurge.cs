using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;

/// <summary>
/// One purge round: removes every idempotency registration older than the
/// configured retention. Age-based and idempotent, so overlapping or retried
/// rounds never remove a registration still inside the contract window.
/// </summary>
internal sealed class IdempotencyPurge(
    NotificationsDbContext db,
    IOptions<IdempotencyPurgeOptions> options,
    TimeProvider timeProvider,
    ILogger<IdempotencyPurge> logger)
{
    /// <summary>Runs one round and returns how many registrations were removed.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset threshold = timeProvider.GetUtcNow() - options.Value.Retention;
        var removed = await db.IdempotencyRegistrations
            .Where(registration => registration.CreatedAt < threshold)
            .ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
        {
            logger.IdempotencyRegistrationsPurged(removed, threshold);
        }

        return removed;
    }
}
