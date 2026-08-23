using StackExchange.Redis;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;

/// <summary>Outcome of one attempt to take the duplicate barrier.</summary>
internal enum DedupeBarrierOutcome
{
    /// <summary>The barrier was free and now belongs to this notification.</summary>
    Acquired,

    /// <summary>The barrier already belongs to this same notification: a reprocessing, not a duplicate.</summary>
    AlreadyHeld,

    /// <summary>Another notification holds the barrier inside the window.</summary>
    Duplicate,

    /// <summary>Redis is unreachable; the rule fails open and records the risk.</summary>
    Unavailable,
}

/// <summary>Atomic duplicate barrier of the policy dedupe window.</summary>
internal interface IDedupeBarrier
{
    Task<DedupeBarrierOutcome> TryAcquireAsync(
        string application,
        string templateKey,
        string recipientId,
        Guid notificationId,
        TimeSpan window,
        CancellationToken cancellationToken);
}

/// <summary>
/// Redis SET NX with the window as TTL over
/// (application, templateKey, recipientId). The stored value is the holding
/// notification id, so a redelivery of the same notification recognizes its
/// own mark. Every Redis failure fails open with an alarm log: a possible
/// duplicate is the accepted, audited risk.
/// </summary>
internal sealed class RedisDedupeBarrier(
    NotificationsRedisConnection redis,
    ILogger<RedisDedupeBarrier> logger) : IDedupeBarrier
{
    public async Task<DedupeBarrierOutcome> TryAcquireAsync(
        string application,
        string templateKey,
        string recipientId,
        Guid notificationId,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = $"{redis.KeyPrefix}dedupe:{application}:{templateKey}:{recipientId}";
        var value = notificationId.ToString("N");
        try
        {
            var acquired = await redis.Database.StringSetAsync(key, value, window, When.NotExists);
            if (acquired)
            {
                return DedupeBarrierOutcome.Acquired;
            }

            RedisValue holder = await redis.Database.StringGetAsync(key);
            return holder.HasValue && string.Equals(holder.ToString(), value, StringComparison.Ordinal)
                ? DedupeBarrierOutcome.AlreadyHeld
                : DedupeBarrierOutcome.Duplicate;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.DedupeBarrierUnavailable(exception);
            return DedupeBarrierOutcome.Unavailable;
        }
    }
}
