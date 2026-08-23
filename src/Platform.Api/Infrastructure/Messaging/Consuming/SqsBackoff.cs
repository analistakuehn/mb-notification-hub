namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// Exponential visibility backoff with jitter for transiently failed
/// messages. The message returns to the queue after the computed delay; the
/// DLQ only ever receives it through the queue's redrive policy.
/// </summary>
internal static class SqsBackoff
{
    /// <summary>
    /// Delay in seconds for the given delivery attempt (1-based). Grows as
    /// base * 2^(attempt - 1), capped at the maximum, with up to half the
    /// value of random jitter subtracted so retries spread out.
    /// </summary>
    internal static int DelaySeconds(int receiveCount, int baseSeconds, int maxSeconds)
    {
        var attempt = Math.Max(1, receiveCount);
        var exponent = Math.Min(attempt - 1, 16);
        var delay = Math.Min((long)baseSeconds << exponent, maxSeconds);
        var jitter = Random.Shared.NextInt64(delay / 2 + 1);
        return (int)Math.Max(1, delay - jitter);
    }
}
