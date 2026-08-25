using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

/// <summary>Outcome of one bucket consumption.</summary>
internal readonly record struct ProviderRateDecision(bool Allowed, TimeSpan RetryAfter)
{
    internal static ProviderRateDecision Allow() => new(true, TimeSpan.Zero);
}

/// <summary>
/// The budget one send spends before it reaches a provider. The seam exists so
/// the decorator that spends it can be exercised over every answer this
/// contract can give, including the refusal, without a store to arrange.
/// </summary>
internal interface IProviderRateBudget
{
    Task<ProviderRateDecision> TryConsumeAsync(string providerKey, CancellationToken cancellationToken);
}

/// <summary>
/// Redis-backed token bucket over one provider's contracted rate, shared by
/// every instance sending through that provider. The refill, the consumption
/// and the deadline run atomically in a small Lua script, so two dispatchers
/// racing for the last token cannot both take it. Every Redis failure fails
/// open with an alarm log, in the same posture the ingestion limiter holds:
/// availability of the send prevails over the control, and the kill switch is
/// the manual compensation. Blocking sends behind an unreachable store would
/// stop the channel for a reason the provider never gave.
/// </summary>
internal sealed class ProviderRateLimiter(
    ProviderRateLimitConnection connection,
    IOptions<ProviderRateLimitOptions> options,
    TimeProvider timeProvider,
    ILogger<ProviderRateLimiter> logger) : IProviderRateBudget
{
    /// <summary>
    /// Refills by elapsed time, spends one token and stores the bucket back
    /// with a deadline. The instant arrives from the caller instead of the
    /// store clock, so the window is the same one the rest of the send path
    /// reads. Token counts travel as strings on purpose: a Lua number handed
    /// to a Redis command loses its fractional part, and a bucket that
    /// truncates never refills below one token per second.
    /// </summary>
    private const string ConsumeTokenScript = """
        local capacity = tonumber(ARGV[1])
        local permitsPerSecond = tonumber(ARGV[2])
        local now = tonumber(ARGV[3])
        local ttl = tonumber(ARGV[4])
        local bucket = redis.call('HMGET', KEYS[1], 'tokens', 'updated')
        local tokens = capacity
        if bucket[1] then
            local elapsed = now - tonumber(bucket[2])
            if elapsed < 0 then
                elapsed = 0
            end
            tokens = math.min(capacity, tonumber(bucket[1]) + (elapsed * permitsPerSecond) / 1000)
        end
        local allowed = 0
        local retryAfter = 0
        if tokens >= 1 then
            tokens = tokens - 1
            allowed = 1
        else
            retryAfter = math.ceil(((1 - tokens) * 1000) / permitsPerSecond)
        end
        redis.call('HSET', KEYS[1], 'tokens', tostring(tokens), 'updated', tostring(now))
        redis.call('PEXPIRE', KEYS[1], ttl)
        return {allowed, retryAfter}
        """;

    /// <summary>
    /// Takes one send's worth of budget from the provider's bucket. A provider
    /// with no contracted rate, and a deployment with no store, are allowed
    /// without a round trip.
    /// </summary>
    public async Task<ProviderRateDecision> TryConsumeAsync(
        string providerKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Value.For(providerKey) is not { } limit) return ProviderRateDecision.Allow();
        if (!connection.IsConfigured) return ProviderRateDecision.Allow();

        try
        {
            RedisResult result = await connection.Database.ScriptEvaluateAsync(
                ConsumeTokenScript,
                [new RedisKey($"{connection.KeyPrefix}rl:provider:{providerKey}")],
                [
                    limit.Capacity,
                    limit.PermitsPerSecond,
                    timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    (long)limit.KeyTtl.TotalMilliseconds,
                ]);
            var values = (RedisResult[])result!;
            if ((long)values[0] == 1) return ProviderRateDecision.Allow();

            var retryAfterMilliseconds = (long)values[1];
            logger.ProviderRateLimitReached(providerKey, limit.PermitsPerSecond);
            return new ProviderRateDecision(false, RetryAfter(retryAfterMilliseconds));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.ProviderRateLimitStoreUnavailable(exception);
            return ProviderRateDecision.Allow();
        }
    }

    /// <summary>
    /// The wait rounded up to whole seconds and never below one. The hint
    /// becomes a queue visibility timeout, whose unit is the second, and a
    /// zero would return the message immediately into the same refusal.
    /// </summary>
    private static TimeSpan RetryAfter(long milliseconds)
        => TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(milliseconds / 1000d)));
}
