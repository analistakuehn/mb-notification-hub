namespace NotificationHub.Api.Modules.ContactConsent.Integration.V1;

/// <summary>
/// Everything a sibling module reads about one recipient in a single
/// resolution: profile preferences, active contact points per channel, the
/// current consent state per purpose and channel, and the active device
/// tokens. Contact values never appear here; a consumer that must address a
/// contact asks for the value explicitly through the dedicated read of
/// <see cref="IRecipientDirectory"/>.
/// </summary>
public sealed record RecipientSnapshot
{
    public required string RecipientId { get; init; }

    /// <summary>Effective IANA timezone, with the platform default already applied when the recipient never declared one.</summary>
    public required string Timezone { get; init; }

    public string? Locale { get; init; }

    /// <summary>Active contact points only; removed values stay internal to the ledger.</summary>
    public required IReadOnlyList<ContactPointSnapshot> ContactPoints { get; init; }

    /// <summary>The latest declared consent state per (purpose, channel).</summary>
    public required IReadOnlyList<ConsentDecision> Consents { get; init; }

    /// <summary>Active device tokens, most recently seen first.</summary>
    public required IReadOnlyList<DeviceRegistration> Devices { get; init; }

    /// <summary>
    /// Contact points currently suppressed by delivery feedback, among the
    /// active ones above. The member rides this snapshot instead of a read of
    /// its own because the resolution already happens once per notification: a
    /// separate read would add a round trip to the hot path and would let a
    /// caller decide over a state other than the one it resolved.
    /// </summary>
    public required IReadOnlyList<SuppressionState> Suppressions { get; init; }
}

/// <summary>
/// One contact point the hub stopped addressing, and why. A suppression is
/// reversible: it stays in force until an operator removes it, and
/// <see cref="Until"/> carries the end of a bounded one when a future rule
/// mints one.
/// </summary>
public sealed record SuppressionState(
    Guid ContactPointId,
    string Channel,
    string Reason,
    DateTimeOffset SuppressedAt,
    DateTimeOffset? Until);

/// <summary>One active contact point, addressable through its id; the value stays encrypted inside the module.</summary>
public sealed record ContactPointSnapshot(Guid ContactPointId, string Channel, bool Verified);

/// <summary>
/// The consent state currently in force for one (purpose, channel) pair: the
/// latest record of the append-only ledger, whichever contact value carried
/// it. A revocation stays visible even after the contact value changed.
/// </summary>
public sealed record ConsentDecision(
    string Purpose,
    string Channel,
    bool Granted,
    string Source,
    string TermsVersion,
    DateTimeOffset RecordedAt);

/// <summary>
/// One active push registration, addressable through its id. The token value
/// stays inside this module: the push fan-out reads the snapshot only for ids
/// and recency, and the dispatcher asks for the routing address at send time
/// through the dedicated reveal read, so every plaintext egress is an
/// explicit call site.
/// </summary>
public sealed record DeviceRegistration(
    Guid DeviceTokenId,
    string Platform,
    string? AppVersion,
    DateTimeOffset LastSeenAt);
