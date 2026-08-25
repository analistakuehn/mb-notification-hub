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
/// Stored vocabulary of the suppression signal a provider failure carries.
/// The classification itself is provider knowledge and belongs to the dispatch
/// side; this module only writes down what that side decided, in the durable
/// spelling, so a reordered enum can never reinterpret a stored row.
/// </summary>
internal static class DeliverySuppressionSignals
{
    internal const string None = "none";
    internal const string HardBounce = "hard-bounce";
    internal const string InvalidDestination = "invalid-destination";

    /// <summary>Durable spelling of one classified signal.</summary>
    internal static string From(SuppressionSignal signal) => signal switch
    {
        SuppressionSignal.None => None,
        SuppressionSignal.HardBounce => HardBounce,
        SuppressionSignal.InvalidDestination => InvalidDestination,
        _ => throw new ArgumentOutOfRangeException(
            nameof(signal), signal, "Sinal de supressão desconhecido."),
    };

    /// <summary>
    /// Reads a durable spelling back. A row written before this column existed
    /// carries the neutral value, and anything this vocabulary does not name
    /// reads neutral as well: an unrecognized spelling must never be promoted
    /// into a decision that stops addressing a person.
    /// </summary>
    internal static SuppressionSignal Parse(string? value) => value switch
    {
        HardBounce => SuppressionSignal.HardBounce,
        InvalidDestination => SuppressionSignal.InvalidDestination,
        _ => SuppressionSignal.None,
    };
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
/// The verified provider bytes are not here. They live once in the
/// <see cref="DeliveryPayload"/> row that <see cref="PayloadId"/> names, because
/// the evidence of an event of a batch is the batch and replicating the batch
/// into each of its own events made the write quadratic in a size the provider
/// chooses.
/// </para>
/// </summary>
internal sealed class DeliveryEvent
{
    private DeliveryEvent()
    {
        ProviderKey = null!;
        ProviderEventId = null!;
        Kind = null!;
        SuppressionSignal = null!;
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

    /// <summary>
    /// What the dispatch side concluded the failure says about the destination.
    /// Stored because the consumer that reports it to the contact ledger runs
    /// off the request and cannot reclassify: the vocabulary of provider codes
    /// belongs to the provider adapters, and a second reading of it here would
    /// be a second, divergent classifier.
    /// </summary>
    public string SuppressionSignal { get; private set; }

    /// <summary>
    /// The stored callback these bytes came in. A logical reference and not a
    /// foreign key, for the reason the correlation columns are logical too and
    /// for one more: a foreign key into a partitioned table would block the
    /// partition drop that a later retention needs.
    /// </summary>
    public Guid PayloadId { get; private set; }

    /// <summary>When the state machine consumed this event; null while it stays stored and unapplied.</summary>
    public DateTimeOffset? AppliedAt { get; private set; }

    /// <summary>
    /// When the suppression signal this row carries reached the contact
    /// ledger, or null while it still owes that report.
    /// <para>
    /// The report cannot join the transaction that applies the event: the
    /// ledger belongs to another context and decides on its own history, and
    /// reporting before the transition commits would suppress a destination on
    /// the strength of a callback that ended up applying nothing. So the report
    /// happens after the commit, and this stamp is what keeps it from being
    /// best effort: an applied row with a signal and no stamp is a report this
    /// hub still owes, and a drain retries it. The ledger keys its idempotency
    /// on the identity of this row, so a retry that races the original settles
    /// as already applied rather than as a second refusal.
    /// </para>
    /// <para>
    /// A signal nothing can be done with is stamped too, for the same reason a
    /// dedupe mark commits for feedback that moved nothing: leaving it empty
    /// would make the drain read the same unreportable rows for the life of the
    /// partition.
    /// </para>
    /// </summary>
    public DateTimeOffset? SuppressionReportedAt { get; private set; }

    public static DeliveryEvent Record(DeliveryEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ProviderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ProviderEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.SuppressionSignal);
        if (draft.PayloadId == Guid.Empty)
        {
            throw new ArgumentException(
                "A evidência de entrega exige o callback que a carregou.", nameof(draft));
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
            SuppressionSignal = draft.SuppressionSignal,
            PayloadId = draft.PayloadId,
            AppliedAt = null,
            SuppressionReportedAt = null,
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

    /// <summary>Durable spelling of the classification the dispatch side made.</summary>
    public required string SuppressionSignal { get; init; }

    /// <summary>Callback row that carries the bytes this event arrived in.</summary>
    public required Guid PayloadId { get; init; }
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
