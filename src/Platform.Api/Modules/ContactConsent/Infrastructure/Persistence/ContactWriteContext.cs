namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;

/// <summary>
/// Provenance of a write that arrived as a consumed bus record.
///
/// <see cref="RecordId"/> is the deduplication identity of the record, stamped
/// inside the very transaction of the effect it guards. The producer's event
/// id is correlation for the trail and never a deduplication key: a legitimate
/// resend of the same declaration mints a new one, and a declaration is
/// desired state, so replaying it changes nothing anyway.
/// </summary>
internal sealed record ContactWriteProvenance
{
    /// <summary>Stable identity of the consumed record.</summary>
    public required string RecordId { get; init; }

    /// <summary>Consumer name recorded with the deduplication mark.</summary>
    public required string Consumer { get; init; }

    /// <summary>Producer-assigned event id, for correlation in the trail.</summary>
    public string? EventId { get; init; }
}

/// <summary>
/// Who is writing, and which record carried the write when it came from the
/// bus. Both travel as an explicit parameter instead of an ambient scope: what
/// decides whether a redelivery repeats an effect belongs in the signature,
/// where whoever reads the call site sees it.
/// </summary>
internal sealed record ContactWriteContext(
    string ActorId,
    string ActorType,
    ContactWriteProvenance? Provenance);
