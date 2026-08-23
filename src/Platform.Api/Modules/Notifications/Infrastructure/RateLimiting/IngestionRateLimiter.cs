using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;
using StackExchange.Redis;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;

/// <summary>Dimension that rejected the request; <c>None</c> means allowed.</summary>
internal enum RateLimitedDimension
{
    None = 0,
    Principal = 1,
    Recipient = 2,
}

/// <summary>Outcome of one rate-limit evaluation.</summary>
internal readonly record struct RateLimitDecision(RateLimitedDimension Dimension, int RetryAfterSeconds)
{
    public bool Allowed => Dimension == RateLimitedDimension.None;

    public static RateLimitDecision Allow() => new(RateLimitedDimension.None, 0);
}

/// <summary>
/// Redis-backed fixed-window counters over the two ingestion dimensions. The
/// increment and the expiry run atomically in a small Lua script, so
/// concurrent requests never leave a counter without a deadline. Every Redis
/// failure fails open with an alarm log: availability of the ingestion
/// prevails over the control, and the manual kill switch is the compensation.
/// </summary>
internal sealed class IngestionRateLimiter(
    NotificationsRedisConnection redis,
    IOptions<IngestionRateLimitOptions> options,
    ILogger<IngestionRateLimiter> logger)
{
    private const string CountWithDeadlineScript = """
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        local ttl = redis.call('PTTL', KEYS[1])
        return {current, ttl}
        """;

    /// <summary>
    /// Evaluates both dimensions for one request. The principal dimension is
    /// evaluated first; the recipient windows are cumulative and the retry
    /// hint is the longest deadline among the exhausted ones.
    /// </summary>
    public async Task<RateLimitDecision> EvaluateAsync(
        string principal,
        string application,
        string recipientId,
        string canonicalClass,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            IngestionRateLimitOptions limits = options.Value;

            if (limits.PerPrincipal.TryGetValue(canonicalClass, out RateWindow? principalWindow))
            {
                (var count, var retryAfter) = await CountAsync(
                    $"{redis.KeyPrefix}rl:principal:{principal}:{canonicalClass}:{principalWindow.WindowSeconds}",
                    principalWindow.WindowSeconds);
                if (count > principalWindow.PermitLimit)
                {
                    return new RateLimitDecision(RateLimitedDimension.Principal, retryAfter);
                }
            }

            if (limits.PerRecipient.TryGetValue(canonicalClass, out List<RateWindow>? recipientWindows))
            {
                var rejected = false;
                var longestRetry = 0;
                foreach (RateWindow window in recipientWindows)
                {
                    (var count, var retryAfter) = await CountAsync(
                        $"{redis.KeyPrefix}rl:recipient:{application}:{recipientId}:{canonicalClass}:{window.WindowSeconds}",
                        window.WindowSeconds);
                    if (count > window.PermitLimit)
                    {
                        rejected = true;
                        longestRetry = Math.Max(longestRetry, retryAfter);
                    }
                }

                if (rejected)
                {
                    return new RateLimitDecision(RateLimitedDimension.Recipient, longestRetry);
                }
            }

            return RateLimitDecision.Allow();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.RateLimitStoreUnavailable(exception);
            return RateLimitDecision.Allow();
        }
    }

    private async Task<(long Count, int RetryAfterSeconds)> CountAsync(string key, int windowSeconds)
    {
        RedisResult result = await redis.Database.ScriptEvaluateAsync(
            CountWithDeadlineScript,
            [new RedisKey(key)],
            [(long)windowSeconds * 1000]);
        var values = (RedisResult[])result!;
        var count = (long)values[0];
        var ttlMilliseconds = (long)values[1];
        var retryAfter = ttlMilliseconds > 0
            ? (int)Math.Ceiling(ttlMilliseconds / 1000d)
            : windowSeconds;
        return (count, retryAfter);
    }
}
