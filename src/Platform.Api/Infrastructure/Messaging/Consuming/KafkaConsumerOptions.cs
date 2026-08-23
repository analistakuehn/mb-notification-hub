using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// Tuning of the bus consumer. The correctness knobs are fixed in code, not
/// here: manual offset commit, cooperative sticky assignment and static
/// membership are what make at-least-once with bounded rebalance true, and a
/// deployment that could turn them off would be a deployment that could lose
/// records.
/// </summary>
public sealed class KafkaConsumerOptions
{
    public const string SectionName = "Platform:Messaging:KafkaConsumer";

    /// <summary>Bootstrap servers of the cluster.</summary>
    public string BootstrapServers { get; init; } = string.Empty;

    /// <summary>
    /// Stable instance identity for static membership. Empty falls back to
    /// dynamic membership, which is correct but pays a full rebalance on every
    /// restart; a deployment sets it from the pod ordinal.
    /// </summary>
    public string GroupInstanceId { get; init; } = string.Empty;

    public string SecurityProtocol { get; init; } = "plaintext";

    public string? SaslMechanism { get; init; }

    public string? SaslUsername { get; init; }

    public string? SaslPassword { get; init; }

    /// <summary>How many records one pass settles before committing their offsets.</summary>
    [Range(1, 500)]
    public int BatchSize { get; init; } = 50;

    /// <summary>How long one poll waits for a record before the pass ends.</summary>
    [Range(10, 30_000)]
    public int PollTimeoutMilliseconds { get; init; } = 1_000;

    /// <summary>
    /// Upper bound on the time between polls. It must stay above the worst
    /// single-record transaction, because a slower one would look like a dead
    /// consumer and trigger a rebalance mid-transaction.
    /// </summary>
    [Range(10_000, 900_000)]
    public int MaxPollIntervalMilliseconds { get; init; } = 300_000;

    /// <summary>How long the broker waits for a heartbeat before declaring the member gone.</summary>
    [Range(1_000, 300_000)]
    public int SessionTimeoutMilliseconds { get; init; } = 45_000;

    /// <summary>Largest record body the consumer accepts; anything above it is permanently invalid.</summary>
    [Range(1_024, 8_388_608)]
    public int MaxBodyBytes { get; init; } = 262_144;

    /// <summary>In-process retries of a transient failure before the partition is paused.</summary>
    [Range(0, 10)]
    public int TransientRetryAttempts { get; init; } = 3;

    /// <summary>First backoff step between in-process retries; it doubles per attempt.</summary>
    [Range(10, 10_000)]
    public int TransientRetryBaseMilliseconds { get; init; } = 200;

    /// <summary>How long a paused partition stays paused before the consumer resumes it.</summary>
    [Range(1, 900)]
    public int PauseSeconds { get; init; } = 30;
}
