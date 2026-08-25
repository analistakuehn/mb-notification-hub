namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// Attempt states this context writes. The Core pipeline only ever writes the
/// first one; the dispatcher owns every transition up to a provider verdict
/// through its optimistic lock over the stored status, so two concurrent
/// claims of the same attempt can never both send. The states past the
/// verdict are written by delivery feedback alone, never by a sender.
/// </summary>
public static class NotificationAttemptStatuses
{
    public const string Queued = "queued";

    /// <summary>A dispatcher claimed the attempt and owns the provider call.</summary>
    public const string Sending = "sending";

    /// <summary>The provider took responsibility for the message.</summary>
    public const string Sent = "sent";

    /// <summary>Definitive failure: the provider rejected, or the target was unusable.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// No conclusive provider verdict (timeout, 5xx): whether the message
    /// arrived is unknown, and reconciliation of a later phase resolves it.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The provider confirmed the message reached the destination. Only
    /// delivery feedback writes it, because acceptance by a provider is not
    /// delivery on any channel that reports one.
    /// </summary>
    public const string Delivered = "delivered";

    /// <summary>The recipient opened the message, which is delivery plus proof of reading.</summary>
    public const string Read = "read";

    /// <summary>
    /// The destination rejected the message. Separate from
    /// <see cref="Failed"/> because the accusation is against the destination
    /// itself, which is what a suppression decision reads.
    /// </summary>
    public const string Bounced = "bounced";
}

/// <summary>
/// One delivery attempt of a notification: the channel the plan chose, the
/// encrypted rendered content the dispatcher will send, and the fallback
/// deadline stamped at enqueue time, never at send time. The provider fields
/// stay empty until a dispatcher claims the attempt.
/// </summary>
public sealed class NotificationAttempt
{
    private NotificationAttempt()
    {
        Channel = null!;
        RenderedContentEncrypted = null!;
        ContentHashFull = null!;
        ContentHashMasked = null!;
        Status = null!;
    }

    public Guid Id { get; private set; }

    public Guid NotificationId { get; private set; }

    /// <summary>
    /// 1-based monotonic creation order among the notification's attempts.
    /// Push fan-out inserts one sibling per device token, so the sequence
    /// orders creation, not delivery-plan steps.
    /// </summary>
    public int Sequence { get; private set; }

    public string Channel { get; private set; }

    /// <summary>Stamped by the dispatcher when it claims the attempt.</summary>
    public string? ProviderKey { get; private set; }

    /// <summary>Contact point the attempt targets; null for push, whose targets are device tokens.</summary>
    public Guid? ContactPointId { get; private set; }

    /// <summary>
    /// Device token a push attempt targets: a logical reference into the
    /// contact directory, stamped by the dispatcher at claim time when it
    /// expands the fan-out. Null on non-push attempts and on a push attempt
    /// the fan-out has not expanded yet.
    /// </summary>
    public Guid? DeviceTokenId { get; private set; }

    public string? ProviderMessageId { get; private set; }

    /// <summary>Envelope-encrypted rendered content, sealed with the application's data key.</summary>
    public byte[] RenderedContentEncrypted { get; private set; }

    /// <summary>Canonical hash of the complete rendered content, computed before any masking.</summary>
    public string ContentHashFull { get; private set; }

    /// <summary>Canonical hash of the masked render, the only form a trail may store.</summary>
    public string ContentHashMasked { get; private set; }

    public string Status { get; private set; }

    public string? ErrorCode { get; private set; }

    /// <summary>Instant after which the tracker requests the fallback step; null on the last step.</summary>
    public DateTimeOffset? FallbackDeadline { get; private set; }

    /// <summary>
    /// Instant the plan step this attempt belongs to was advanced, stamped on
    /// every attempt of the step by the single claim that won it. Null while
    /// the step has not advanced. The claim is per step and not per attempt on
    /// purpose: a push fan-out creates siblings that share the step's absolute
    /// deadline, so two expired siblings would otherwise each ask for the same
    /// next step and the recipient would get two messages.
    /// </summary>
    public DateTimeOffset? PlanAdvancedAt { get; private set; }

    /// <summary>
    /// Instant this attempt last entered its current status, stamped by every
    /// writer of a transition. It answers how long the attempt has been where
    /// it is, which is the only question an age-driven scan can ask: the
    /// creation instant answers a different one, and a row that changed status
    /// three times would read as young or old by accident.
    /// <para>
    /// Null on every row written before the column existed. Those rows never
    /// match an age predicate, which is deliberate: a scan must not act on an
    /// age it cannot compute, and the reconciliation of a later slice is what
    /// resolves the ones that were already parked when this column arrived.
    /// </para>
    /// </summary>
    public DateTimeOffset? StatusChangedAt { get; private set; }

    /// <summary>
    /// Instant the deadline scan last wrote a fallback trigger for this
    /// attempt, and the whole of what keeps that scan from writing one per
    /// round. The plan claim cannot serve here: it belongs to the handler, and
    /// a scan that stamped it would make the handler read the step as already
    /// advanced and drop the very trigger the scan just asked for.
    /// <para>
    /// It is a window and not a permanent flag on purpose. A trigger that
    /// never reaches the handler (a poisoned message, a dead letter) would
    /// otherwise park the step forever with no one left to ask; letting the
    /// stamp age out puts the attempt back in front of the scan. Duplication
    /// is not the risk that trade buys, because the single claim in the
    /// handler is what decides the advance, whatever asked for it.
    /// </para>
    /// </summary>
    public DateTimeOffset? FallbackRequestedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static NotificationAttempt Queue(NotificationAttemptDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ContentHashFull);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ContentHashMasked);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(draft.Sequence);
        if (draft.RenderedContentEncrypted is not { Length: > 0 })
        {
            throw new ArgumentException(
                "O conteúdo renderizado cifrado é obrigatório em um attempt enfileirado.", nameof(draft));
        }

        return new NotificationAttempt
        {
            Id = Guid.CreateVersion7(),
            NotificationId = draft.NotificationId,
            Sequence = draft.Sequence,
            Channel = draft.Channel,
            ProviderKey = null,
            ContactPointId = draft.ContactPointId,
            DeviceTokenId = draft.DeviceTokenId,
            ProviderMessageId = null,
            RenderedContentEncrypted = draft.RenderedContentEncrypted,
            ContentHashFull = draft.ContentHashFull,
            ContentHashMasked = draft.ContentHashMasked,
            Status = NotificationAttemptStatuses.Queued,
            ErrorCode = null,
            FallbackDeadline = draft.FallbackDeadline
                ?? (draft.FallbackTimeout is { } timeout ? draft.QueuedAt + timeout : null),
            PlanAdvancedAt = null,
            StatusChangedAt = draft.QueuedAt,
            FallbackRequestedAt = null,
            SentAt = null,
            DeliveredAt = null,
            CreatedAt = draft.QueuedAt,
        };
    }
}

/// <summary>Validated inputs of one queued attempt, gathered by the pipeline commit.</summary>
public sealed record NotificationAttemptDraft
{
    public required Guid NotificationId { get; init; }

    public required int Sequence { get; init; }

    public required string Channel { get; init; }

    public Guid? ContactPointId { get; init; }

    /// <summary>Device token of a push sibling; null until the fan-out expansion stamps one.</summary>
    public Guid? DeviceTokenId { get; init; }

    public required byte[] RenderedContentEncrypted { get; init; }

    public required string ContentHashFull { get; init; }

    public required string ContentHashMasked { get; init; }

    /// <summary>Wait before the fallback step of the plan; null when the plan has no next step.</summary>
    public TimeSpan? FallbackTimeout { get; init; }

    /// <summary>
    /// Absolute fallback instant copied verbatim onto a push sibling, so every
    /// sibling shares the step's deadline instead of recomputing it from its
    /// own creation instant. Takes precedence over <see cref="FallbackTimeout"/>.
    /// </summary>
    public DateTimeOffset? FallbackDeadline { get; init; }

    public required DateTimeOffset QueuedAt { get; init; }
}
