namespace NotificationHub.Api.Infrastructure.Messaging;

/// <summary>
/// One pending or sent row of the platform outbox. The row is written by
/// <see cref="IOutboxWriter"/> inside the producing module's transaction; the
/// relay reads pending rows by priority class, publishes them to their
/// destination, and stamps <see cref="SentAt"/>. The entity exists for
/// migrations, maintenance and reads; production writes go through the writer
/// contract, on the caller's own transaction.
/// </summary>
public sealed class OutboxMessage
{
    // EF Core materialization: fields are populated from the store.
    private OutboxMessage()
    {
        Destination = null!;
        Transport = null!;
        EventType = null!;
        MessageKey = null!;
        HeadersJson = null!;
        PayloadJson = null!;
        PriorityClass = null!;
    }

    public Guid Id { get; }

    /// <summary>Logical destination the relay publishes to (queue or topic name).</summary>
    public string Destination { get; }

    /// <summary>
    /// Transport of the destination, from <see cref="OutboxTransports"/>. The
    /// relay claims one transport lane at a time, so an unavailable bus never
    /// blocks the queue rows of the same priority band.
    /// </summary>
    public string Transport { get; }

    /// <summary>Type of the enveloped message, mirrored from the envelope for relay filtering.</summary>
    public string EventType { get; }

    /// <summary>Ordering key the relay hands to the destination (for example the recipient id).</summary>
    public string MessageKey { get; }

    /// <summary>Transport headers as a JSON object (tracing context and friends).</summary>
    public string HeadersJson { get; }

    /// <summary>Full message envelope as JSON; the relay publishes it as the message body.</summary>
    public string PayloadJson { get; }

    /// <summary>Priority class the relay orders its reads by.</summary>
    public string PriorityClass { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Stamped by the relay after a successful publish; null while pending.</summary>
    public DateTimeOffset? SentAt { get; }
}
