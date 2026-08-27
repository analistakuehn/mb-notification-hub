namespace NotificationHub.Api.Modules.ContactConsent.Integration.V1;

/// <summary>
/// The canonical key of a consent purpose, and the only spelling a stance may
/// be recorded, resolved, or compared under.
///
/// The vocabulary stays open on purpose. A purpose is minted outside the hub:
/// the registration system declares it on the bus and a class policy names the
/// one it rides on, so a closed list here would turn every new purpose into a
/// hub deploy and would refuse an opt-out the declaring system is obliged to
/// record. Refusing a legitimate revocation is worse than the ambiguity a
/// closed list would remove. What the hub owns instead is the key: casing and
/// surrounding whitespace carry no meaning, so they are removed before the
/// value is ever used as one.
///
/// Every side of a comparison canonicalizes: the consent side because the
/// aggregate records the canonical form, and the policy side because its
/// <c>consentPurpose</c> is authored in another module against a wider cap and
/// its own validation.
/// </summary>
public static class ConsentPurpose
{
    /// <summary>Ceiling of the canonical key, matching the ledger column.</summary>
    public const int MaxLength = 100;

    /// <summary>
    /// The canonical key of a declared purpose. A value that declares nothing
    /// canonicalizes to the empty string, which matches no stance; the caller
    /// decides whether the absence of a purpose means anything.
    /// </summary>
    public static string Canonicalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
