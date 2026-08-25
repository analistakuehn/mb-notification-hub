namespace NotificationHub.Api.Modules.Notifications.Integration.V1;

/// <summary>
/// What this hub is able to learn about the fate of a message on one channel.
/// The distinction is a property of the channel and of its providers, not a
/// quality of the measurement, and a consumer that ignores it reads silence as
/// failure.
/// </summary>
public static class DeliveryConfirmationSources
{
    /// <summary>
    /// The provider reports what became of the message, through a callback or
    /// through a later lookup. Delivery and bounce are facts on this channel.
    /// </summary>
    public const string ProviderFeedback = "provider-feedback";

    /// <summary>
    /// The strongest signal this hub will ever hold is the provider accepting
    /// the message. The provider reports no delivery and offers no lookup
    /// afterwards, so whether the message arrived stays unknown by design, and
    /// no later phase of this platform can close that gap.
    /// </summary>
    public const string AcceptanceOnly = "acceptance-only";
}

/// <summary>
/// Aggregate outcome of one window, composed inside the owning module. Every
/// member is a count over rows this context owns; nothing here identifies a
/// person, a destination or a message.
/// </summary>
public sealed record NotificationOutcomeSummary
{
    public required DateTimeOffset FromInclusive { get; init; }

    public required DateTimeOffset ToExclusive { get; init; }

    /// <summary>Notifications created inside the window, by canonical class.</summary>
    public required IReadOnlyList<NotificationClassVolume> VolumesByClass { get; init; }

    /// <summary>Attempts queued inside the window, by channel.</summary>
    public required IReadOnlyList<NotificationChannelOutcome> OutcomesByChannel { get; init; }

    /// <summary>
    /// Recorded policy refusals inside the window, by canonical reason. The
    /// source is the per-rule decision this module stores, so the count is of
    /// rule decisions and not of notifications: one notification refused by
    /// one rule contributes exactly one refusal.
    /// </summary>
    public required IReadOnlyList<NotificationRejectionCount> RejectionsByReason { get; init; }
}

/// <summary>Volume of one class inside the window, with the states its notifications reached.</summary>
public sealed record NotificationClassVolume
{
    public required string Class { get; init; }

    /// <summary>Notifications of this class created inside the window, whatever their state now.</summary>
    public required long Requested { get; init; }

    /// <summary>
    /// The state each of those notifications is in when the window is read.
    /// A lifecycle state moves after the fact, so this is the state at
    /// composition time and never the state at the end of the window.
    /// </summary>
    public required IReadOnlyList<NotificationStatusCount> ByStatus { get; init; }
}

/// <summary>Count of one lifecycle state, in the durable spelling the store holds.</summary>
public sealed record NotificationStatusCount
{
    public required string Status { get; init; }

    public required long Count { get; init; }
}

/// <summary>
/// Outcome of one channel inside the window. The counts partition the attempts
/// exactly once: attempts equals accepted plus failed plus unknown plus
/// pending, so a consumer can check the arithmetic instead of trusting it.
/// </summary>
public sealed record NotificationChannelOutcome
{
    public required string Channel { get; init; }

    /// <summary>One member of <see cref="DeliveryConfirmationSources"/>.</summary>
    public required string DeliveryConfirmation { get; init; }

    /// <summary>Attempts queued inside the window on this channel.</summary>
    public required long Attempts { get; init; }

    /// <summary>
    /// Attempts a provider took responsibility for: it accepted the message,
    /// or reported a verdict that presupposes acceptance. It is the only
    /// denominator under which a delivery rate means anything, because an
    /// attempt that never left cannot be delivered or refused.
    /// </summary>
    public required long AcceptedByProvider { get; init; }

    /// <summary>Attempts a provider confirmed as arrived, reading included.</summary>
    public required long Delivered { get; init; }

    /// <summary>Attempts the destination itself refused.</summary>
    public required long Bounced { get; init; }

    /// <summary>Attempts that failed definitively, provider refusal and unusable target alike.</summary>
    public required long Failed { get; init; }

    /// <summary>
    /// Attempts with no conclusive verdict. On a channel whose providers
    /// report nothing afterwards this is not a measurement gap that a later
    /// round closes: it is the answer.
    /// </summary>
    public required long Unknown { get; init; }

    /// <summary>Attempts still in flight when the window was read.</summary>
    public required long Pending { get; init; }
}

/// <summary>Count of one canonical refusal reason inside the window.</summary>
public sealed record NotificationRejectionCount
{
    public required string Reason { get; init; }

    public required long Count { get; init; }
}
