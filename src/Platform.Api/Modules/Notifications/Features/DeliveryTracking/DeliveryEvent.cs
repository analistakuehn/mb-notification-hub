using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking;

/// <summary>
/// Stored vocabulary of delivery feedback. The canonical enum of the provider
/// contract is the wire form; these strings are the durable form, written once
/// and read by evidence surfaces years later, so they are spelled out here
/// instead of persisted as ordinals that a reordered enum would silently
/// reinterpret.
/// </summary>
internal static class DeliveryEventKinds
{
    internal const string Sent = "sent";
    internal const string Delivered = "delivered";
    internal const string Read = "read";
    internal const string Failed = "failed";
    internal const string Bounced = "bounced";

    /// <summary>Durable spelling of one canonical kind.</summary>
    internal static string From(DeliveryFeedbackKind kind) => kind switch
    {
        DeliveryFeedbackKind.Sent => Sent,
        DeliveryFeedbackKind.Delivered => Delivered,
        DeliveryFeedbackKind.Read => Read,
        DeliveryFeedbackKind.Failed => Failed,
        DeliveryFeedbackKind.Bounced => Bounced,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "Tipo de feedback de entrega desconhecido."),
    };

    /// <summary>Reads a durable spelling back into the canonical kind.</summary>
    internal static bool TryParse(string value, out DeliveryFeedbackKind kind)
    {
        switch (value)
        {
            case Sent:
                kind = DeliveryFeedbackKind.Sent;
                return true;
            case Delivered:
                kind = DeliveryFeedbackKind.Delivered;
                return true;
            case Read:
                kind = DeliveryFeedbackKind.Read;
                return true;
            case Failed:
                kind = DeliveryFeedbackKind.Failed;
                return true;
            case Bounced:
                kind = DeliveryFeedbackKind.Bounced;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}

/// <summary>
/// One piece of provider feedback exactly as this hub received it, kept as
/// evidence independently of whether it could be applied. The row is written
/// inside the request that received the callback and never rewritten
/// afterwards, except for the single stamp that records when the state
/// machine consumed it.
/// <para>
/// The partition column is <see cref="ReceivedAt"/> and not
/// <see cref="OccurredAt"/> on purpose: the provider dates its own events and
/// may date one backwards, which would place the row outside every
/// provisioned partition and fail the insert of a callback this hub has no
/// right to refuse.
/// </para>
/// <para>
/// <see cref="PayloadEncrypted"/> holds the verified provider bytes sealed by
/// the envelope cipher under a key scope of the tracker's own. The raw body
/// carries the destination in the clear, this module forbids personal data at
/// rest in the clear, and the owning application is not necessarily known at
/// insert time: a lookup to learn it would spend the whole latency budget of
/// the callback.
/// </para>
/// </summary>
internal sealed class DeliveryEvent
{
    private DeliveryEvent()
    {
        ProviderKey = null!;
        ProviderEventId = null!;
        Kind = null!;
        PayloadEncrypted = null!;
    }

    public Guid Id { get; private set; }

    /// <summary>Instant this hub took the callback; the partition column.</summary>
    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>Attempt this feedback describes; absent until correlation resolves one.</summary>
    public Guid? AttemptId { get; private set; }

    public Guid? NotificationId { get; private set; }

    public string ProviderKey { get; private set; }

    /// <summary>Deduplication identity of the event inside its provider.</summary>
    public string ProviderEventId { get; private set; }

    /// <summary>Provider-side message identity, the fallback correlation route.</summary>
    public string? ProviderMessageId { get; private set; }

    public string Kind { get; private set; }

    /// <summary>Instant the provider says the event happened; never a partition key.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    public string? ErrorCode { get; private set; }

    /// <summary>Envelope-encrypted verified provider payload.</summary>
    public byte[] PayloadEncrypted { get; private set; }

    /// <summary>When the state machine consumed this event; null while it stays stored and unapplied.</summary>
    public DateTimeOffset? AppliedAt { get; private set; }

    public static DeliveryEvent Record(DeliveryEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ProviderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ProviderEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Kind);
        if (draft.PayloadEncrypted is not { Length: > 0 })
        {
            throw new ArgumentException(
                "A evidência de entrega exige o payload do provedor selado.", nameof(draft));
        }

        return new DeliveryEvent
        {
            Id = Guid.CreateVersion7(),
            ReceivedAt = draft.ReceivedAt,
            AttemptId = draft.AttemptId,
            NotificationId = draft.NotificationId,
            ProviderKey = draft.ProviderKey,
            ProviderEventId = draft.ProviderEventId,
            ProviderMessageId = draft.ProviderMessageId,
            Kind = draft.Kind,
            OccurredAt = draft.OccurredAt,
            ErrorCode = draft.ErrorCode,
            PayloadEncrypted = draft.PayloadEncrypted,
            AppliedAt = null,
        };
    }
}

/// <summary>Validated inputs of one recorded piece of provider feedback.</summary>
internal sealed record DeliveryEventDraft
{
    public required DateTimeOffset ReceivedAt { get; init; }

    public Guid? AttemptId { get; init; }

    public Guid? NotificationId { get; init; }

    public required string ProviderKey { get; init; }

    public required string ProviderEventId { get; init; }

    public string? ProviderMessageId { get; init; }

    public required string Kind { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? ErrorCode { get; init; }

    public required byte[] PayloadEncrypted { get; init; }
}

/// <summary>
/// Deduplication ledger of provider callbacks, deliberately outside the
/// partitioning: a unique key over a partitioned table would have to carry the
/// partition column, and a callback redelivered days later must collide with
/// the first delivery regardless of when either arrived. The row keeps no
/// provider payload, only the identity that must not be honoured twice, which
/// is why a short retention can drop it without losing evidence.
/// </summary>
internal sealed class ProviderEventDedupe
{
    private ProviderEventDedupe()
    {
        Provider = null!;
        ProviderEventId = null!;
    }

    public string Provider { get; private set; }

    public string ProviderEventId { get; private set; }

    public DateTimeOffset ProcessedAt { get; private set; }
}
