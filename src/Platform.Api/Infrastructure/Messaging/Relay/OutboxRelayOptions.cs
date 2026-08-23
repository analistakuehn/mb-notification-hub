using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Tuning of one relay instance. Every default supports a functional single
/// instance; production profiles override cadence, batch and concurrency, and
/// dedicated instances restrict <see cref="Bands"/> to serve one class alone.
/// </summary>
public sealed class OutboxRelayOptions
{
    public const string SectionName = "Platform:Messaging:Relay";

    /// <summary>
    /// Idle wait between passes. A pass that published without failures loops
    /// again immediately, so the interval only paces an empty or failing
    /// outbox, never a busy one.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:05:00")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Rows claimed per batch with <c>FOR UPDATE SKIP LOCKED</c>.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; init; } = 100;

    /// <summary>Concurrent publish calls per destination group.</summary>
    [Range(1, 64)]
    public int PublishConcurrency { get; init; } = 4;

    /// <summary>
    /// Bands this instance drains; empty means all of them. A dedicated
    /// critical instance configures <c>["auth", "critical"]</c>; the drain
    /// order stays fixed regardless of the configured order. Empty is the
    /// default (instead of listing every band) because configuration binding
    /// appends bound array items to a non-empty property default.
    /// </summary>
    public string[] Bands { get; init; } = [];

    /// <summary>
    /// Transport lanes this instance drains; empty means all of them. An
    /// instance dedicated to the internal queues configures <c>["sqs"]</c>.
    /// Empty is the default for the same binding reason as
    /// <see cref="Bands"/>.
    /// </summary>
    public string[] Transports { get; init; } = [];
}
