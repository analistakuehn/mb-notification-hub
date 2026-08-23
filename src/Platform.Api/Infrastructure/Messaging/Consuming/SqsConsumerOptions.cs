using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// Tuning of one consuming role. Every default supports a functional single
/// instance with long polling; tests shorten the wait so a pass returns fast.
/// </summary>
public sealed class SqsConsumerOptions
{
    public const string SectionName = "Platform:Messaging:Consumer";

    /// <summary>Long-polling wait per receive call.</summary>
    [Range(0, 20)]
    public int WaitTimeSeconds { get; init; } = 20;

    /// <summary>Messages received per call; ten is the SQS ceiling.</summary>
    [Range(1, 10)]
    public int BatchSize { get; init; } = 10;

    /// <summary>
    /// Processing slots shared by every queue of the role. Priority applies
    /// here, at slot allocation, never at polling order.
    /// </summary>
    [Range(1, 64)]
    public int Concurrency { get; init; } = 8;

    /// <summary>First visibility extension of a transiently failed message.</summary>
    [Range(1, 900)]
    public int BackoffBaseSeconds { get; init; } = 5;

    /// <summary>Ceiling of the exponential visibility backoff.</summary>
    [Range(1, 43_200)]
    public int BackoffMaxSeconds { get; init; } = 900;

    /// <summary>Pause after a failed or empty pass, so a broken queue never spins hot.</summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:05:00")]
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(1);
}
