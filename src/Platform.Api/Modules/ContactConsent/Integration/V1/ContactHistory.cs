namespace NotificationHub.Api.Modules.ContactConsent.Integration.V1;

/// <summary>
/// One contact point described for reconstruction: the masked value, the
/// channel, whether the point is still active, and the removal stamp when it is
/// not. The plaintext never crosses this boundary and neither does the
/// deterministic value hash, which would hand out a stable correlatable
/// pseudonym.
/// </summary>
/// <remarks>
/// The removal stamp is the only lifecycle instant this module records for a
/// contact point today; a creation or verification instant would need columns
/// the ledger does not have, and inventing them from a row identifier would be
/// a claim about history rather than a record of it. When those columns do
/// arrive, a row that predates them stays null forever and the member is
/// omitted: this contract never answers zero and never answers an epoch,
/// because both would state a fact the ledger does not hold.
/// </remarks>
public sealed record HistoricalContactPoint
{
    public required Guid ContactPointId { get; init; }

    public required string Channel { get; init; }

    /// <summary>Value reduced by the channel's masking rule; never the plaintext.</summary>
    public required string MaskedValue { get; init; }

    public required bool Verified { get; init; }

    public required bool Active { get; init; }

    /// <summary>Instant a declaration stopped listing this value; null while it is active.</summary>
    public DateTimeOffset? RemovedAt { get; init; }
}

/// <summary>
/// One push registration described for reconstruction. The routing token never
/// appears, not even masked: a token is a credential, and no audit question is
/// answered by holding one.
/// </summary>
/// <remarks>
/// The reason the provider gave for an invalidation is deliberately absent, and
/// it is not an oversight to be corrected later. It is a trail fact, recorded by
/// the lifecycle write as an audit event over this registration, and an evidence
/// composer reads it from the trail by subject. Promoting it to a column here
/// would create a second home for one truth, and a column drifting from the
/// canonical text it duplicates is exactly what the chain verification exists to
/// catch.
/// </remarks>
public sealed record HistoricalDeviceRegistration
{
    public required Guid DeviceTokenId { get; init; }

    public required string Platform { get; init; }

    public string? AppVersion { get; init; }

    public required DateTimeOffset RegisteredAt { get; init; }

    public required DateTimeOffset LastSeenAt { get; init; }

    public required bool Active { get; init; }

    /// <summary>Instant provider feedback declared the token dead; null while it is active.</summary>
    public DateTimeOffset? InvalidatedAt { get; init; }
}

/// <summary>
/// One entry of the append-only consent ledger, exactly as it was recorded. The
/// read hands over every entry of the window and never the state in force at an
/// instant: computing what was valid at a past moment is the auditor's reading
/// of the ledger, and the hub stating it would be the hub interpreting evidence
/// it is supposed to present.
/// </summary>
public sealed record ConsentLedgerEntry
{
    public required Guid ContactPointId { get; init; }

    /// <summary>Channel of the contact point the entry is anchored on.</summary>
    public required string Channel { get; init; }

    public required string Purpose { get; init; }

    public required bool Granted { get; init; }

    public required string Source { get; init; }

    /// <summary>Stable identity of the principal that declared the state.</summary>
    public required string ActorId { get; init; }

    public required string TermsVersion { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }
}
