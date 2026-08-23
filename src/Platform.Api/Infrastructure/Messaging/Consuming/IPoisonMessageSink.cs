namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>Everything the discard trail records about one poison message. Never the raw body.</summary>
public sealed record PoisonMessage
{
    public required string QueueName { get; init; }

    /// <summary>Transport message id assigned by SQS.</summary>
    public required string SqsMessageId { get; init; }

    /// <summary>Envelope message id, when the body parsed far enough to expose one.</summary>
    public Guid? EnvelopeMessageId { get; init; }

    /// <summary>Envelope type, when the body parsed far enough to expose one.</summary>
    public string? EventType { get; init; }

    public int? SchemaVersion { get; init; }

    /// <summary>Stable discard reason.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Records one permanently invalid message before the consumer deletes it:
/// the discard trail and the processed mark commit in one transaction, so the
/// message never silently disappears and a redelivery of the same body lands
/// on the dedupe mark. Implementations live with the consuming role, because
/// the audit trail contract belongs to a module, never to this platform layer.
/// </summary>
public interface IPoisonMessageSink
{
    Task RecordDiscardAsync(PoisonMessage message, CancellationToken cancellationToken);
}
