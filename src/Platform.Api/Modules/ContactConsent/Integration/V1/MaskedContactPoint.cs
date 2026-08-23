namespace NotificationHub.Api.Modules.ContactConsent.Integration.V1;

/// <summary>
/// One contact point reduced to the form a query surface may show: the
/// addressable identity, its channel, the masked value computed inside the
/// owning module, and whether the point is still active. The plaintext never
/// crosses this boundary, and neither does the deterministic value hash, which
/// would hand out a stable correlatable pseudonym.
/// </summary>
/// <param name="ContactPointId">Identity of the contact point the caller asked about.</param>
/// <param name="Channel">Canonical channel of the point.</param>
/// <param name="MaskedValue">Value reduced by the channel's masking rule; never the plaintext.</param>
/// <param name="Active">
/// False when the point was already stamped removed. A removed point still
/// answers, because the question a historical read asks is where a message
/// went, not where a message would go now.
/// </param>
public sealed record MaskedContactPoint(
    Guid ContactPointId,
    string Channel,
    string MaskedValue,
    bool Active);
