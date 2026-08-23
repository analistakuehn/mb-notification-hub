using NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;
using StackExchange.Redis;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;

/// <summary>One remembered acceptance: the notification id and the payload hash it answered.</summary>
internal readonly record struct RememberedAcceptance(Guid NotificationId, string PayloadHash);

/// <summary>
/// Redis fast path of the idempotency contract: remembers an accepted
/// (application, idempotency key) for the contract window so an obvious
/// replay skips the database. The entry is written only after the database
/// commit, and an unreadable, absent or malformed entry is a miss, because
/// the authority is always the unique key of the idempotency table. Every
/// Redis failure fails open with an alarm log.
/// </summary>
internal sealed class IdempotencyFastPath(
    NotificationsRedisConnection redis,
    ILogger<IdempotencyFastPath> logger)
{
    /// <summary>How long a remembered acceptance answers replays; mirrors the purge window of the table.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public async Task<RememberedAcceptance?> FindAsync(
        string application,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            RedisValue value = await redis.Database.StringGetAsync(Key(application, idempotencyKey));
            if (value.IsNullOrEmpty)
            {
                return null;
            }

            var parts = value.ToString().Split('|');
            if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out Guid notificationId))
            {
                return null;
            }

            return new RememberedAcceptance(notificationId, parts[1]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.IdempotencyStoreUnavailable(exception);
            return null;
        }
    }

    /// <summary>Remembers an acceptance; called strictly after the database commit.</summary>
    public async Task RememberAsync(
        string application,
        string idempotencyKey,
        RememberedAcceptance acceptance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await redis.Database.StringSetAsync(
                Key(application, idempotencyKey),
                $"{acceptance.NotificationId:N}|{acceptance.PayloadHash}",
                Window);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.IdempotencyStoreUnavailable(exception);
        }
    }

    private string Key(string application, string idempotencyKey)
        => $"{redis.KeyPrefix}idem:{application}:{idempotencyKey}";
}
