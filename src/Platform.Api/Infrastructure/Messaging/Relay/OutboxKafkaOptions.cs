using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Producer settings of the Kafka lane of the relay. The durability knobs are
/// not configurable: <c>acks=all</c> with an idempotent producer is the only
/// setting under which a stamped <c>sent_at</c> means the broker accepted the
/// record, and the relay's whole contract rests on that. What remains
/// configurable is where the cluster is, how long a record may wait, and how
/// hard the client batches.
/// </summary>
public sealed class OutboxKafkaOptions
{
    public const string SectionName = "Platform:Messaging:Kafka";

    /// <summary>Bootstrap servers; empty means the relay has no Kafka lane.</summary>
    public string BootstrapServers { get; init; } = string.Empty;

    /// <summary>Client id reported to the broker; helps attribute lag and quota.</summary>
    public string ClientId { get; init; } = "notification-hub-relay";

    /// <summary>Security protocol accepted by the broker (plaintext, ssl, sasl_ssl, sasl_plaintext).</summary>
    public string SecurityProtocol { get; init; } = "plaintext";

    public string? SaslMechanism { get; init; }

    public string? SaslUsername { get; init; }

    public string? SaslPassword { get; init; }

    /// <summary>Linger window that lets the client batch without hurting the latency budget.</summary>
    [Range(0, 1000)]
    public int LingerMilliseconds { get; init; } = 5;

    /// <summary>Batch size in bytes; raised well above the client default because the payloads are small.</summary>
    [Range(16_384, 8_388_608)]
    public int BatchSizeBytes { get; init; } = 262_144;

    /// <summary>
    /// Upper bound on how long one record may stay in the client before the
    /// publish is reported failed and the row stays pending. Bounded on
    /// purpose: an unbounded wait would hide a broker outage behind a growing
    /// in-memory queue instead of surfacing it on the pending backlog.
    /// </summary>
    [Range(1_000, 300_000)]
    public int DeliveryTimeoutMilliseconds { get; init; } = 30_000;

    /// <summary>How long the shutdown flush waits for outstanding reports.</summary>
    [Range(1_000, 60_000)]
    public int FlushTimeoutMilliseconds { get; init; } = 10_000;
}
