namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// Header names every dead-letter record carries, whatever the topic pair it
/// belongs to: the refusal reason, the coordinates of the source record, the
/// instant of the refusal, the tracing context, and whether the published body
/// is the original one or a redacted form of it.
///
/// These names live on the platform because an operator reads them the same
/// way on every dead-letter topic. The diagnostics that only make sense inside
/// one contract stay with the module that owns that contract.
/// </summary>
public static class DeadLetterHeaders
{
    /// <summary>Stable refusal reason, from the vocabulary of the refusing contract.</summary>
    public const string Reason = "reason";

    public const string SourceTopic = "sourceTopic";

    public const string SourcePartition = "sourcePartition";

    public const string SourceOffset = "sourceOffset";

    public const string OccurredAt = "occurredAt";

    /// <summary>
    /// False only when the published body is the source record byte for byte.
    /// A true here means the record is evidence of a refusal, never a copy a
    /// redrive could replay.
    /// </summary>
    public const string Redacted = "redacted";

    /// <summary>W3C tracing context of the refused record.</summary>
    public const string Traceparent = "traceparent";
}
