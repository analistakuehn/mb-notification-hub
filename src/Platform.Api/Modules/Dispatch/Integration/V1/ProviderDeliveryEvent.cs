namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Canonical delivery feedback vocabulary. Each provider speaks its own
/// dialect with a different number of terms; adapters collapse every dialect
/// into exactly one of these values so the state machine that consumes the
/// feedback never reads a provider word. The set is deliberately coarse: a
/// term that no consumer can act on differently is not worth a member.
/// </summary>
public enum DeliveryFeedbackKind
{
    /// <summary>The provider took the message and has not decided yet.</summary>
    Sent = 0,

    /// <summary>The provider confirmed the message reached the destination.</summary>
    Delivered = 1,

    /// <summary>The recipient opened or read the message, which implies delivery.</summary>
    Read = 2,

    /// <summary>
    /// The message will not be delivered for a reason that does not accuse
    /// the destination itself: carrier refusal, provider block, cancellation.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// The destination rejected the message. Whether the rejection is
    /// permanent is a separate judgement carried by
    /// <see cref="SuppressionSignal"/>, because a soft bounce and a hard
    /// bounce arrive under the same provider word.
    /// </summary>
    Bounced = 4,
}

/// <summary>
/// What one delivery failure says about the destination itself, and only
/// about the destination. The distinction exists because suppressing a
/// contact point is close to irreversible for the person behind it: a
/// transient failure must never reach the ledger, so anything the adapter
/// cannot classify with the configured vocabulary stays
/// <see cref="None"/> rather than guessing.
/// </summary>
public enum SuppressionSignal
{
    /// <summary>Nothing durable can be concluded about the destination.</summary>
    None = 0,

    /// <summary>
    /// The destination refuses this kind of message permanently: an opt-out
    /// on record, or a mailbox that rejects definitively.
    /// </summary>
    HardBounce = 1,

    /// <summary>The destination does not exist, which no retry can fix.</summary>
    InvalidDestination = 2,
}

/// <summary>
/// One normalized piece of delivery feedback about one attempt. The same
/// record carries feedback pushed by a webhook and feedback pulled by a later
/// query on purpose, so the two paths cannot drift into two state machines.
/// <para>
/// <c>ProviderEventId</c> is the deduplication key inside a provider: the
/// provider's own event identifier when it mints one, and otherwise a value
/// the adapter derives so that redelivering the same callback yields the same
/// key. <c>Correlation</c> is absent whenever the provider echoes nothing
/// back, in which case the consumer correlates through
/// <c>ProviderMessageId</c>. No destination and no content ever appear here:
/// the record is persisted and later re-read as evidence.
/// </para>
/// </summary>
public sealed record ProviderDeliveryEvent(
    string ProviderKey,
    string ProviderEventId,
    DeliveryFeedbackKind Kind,
    DateTimeOffset OccurredAt,
    string? ProviderMessageId,
    DispatchCorrelation? Correlation,
    string? ErrorCode,
    SuppressionSignal Signal);
