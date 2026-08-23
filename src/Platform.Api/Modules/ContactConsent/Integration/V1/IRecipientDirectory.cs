using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Integration.V1;

/// <summary>
/// Degradation tolerance of one directory read. The caller states, per read,
/// whether the last known snapshot may answer when the module's local read
/// fails; the module never decides that trade-off for a class it cannot see.
/// </summary>
public enum RecipientReadFallback
{
    /// <summary>A local read failure propagates; the caller's retry owns the degradation.</summary>
    None = 0,

    /// <summary>
    /// A local read failure may be answered with the last known snapshot,
    /// bounded by the module's staleness ceiling. Reserved for flows where
    /// losing the message costs more than acting on stale contact data.
    /// </summary>
    LastKnown = 1,
}

/// <summary>
/// In-process read surface of the contact and consent source of truth for
/// sibling modules. The snapshot answers resolution without ever exposing a
/// contact value; the value of one contact point leaves the module only
/// through the dedicated reveal read, decrypted inside the module, so every
/// plaintext egress is an explicit, searchable call site. Suppression is not
/// part of this version: when the suppression ledger arrives, its read joins
/// this contract as a new member or a V2 surface.
/// </summary>
public interface IRecipientDirectory
{
    /// <summary>
    /// Resolves the full snapshot of one recipient: profile with the default
    /// timezone applied, active contact points, current consent per purpose
    /// and channel, and active device tokens. An unknown recipient fails as
    /// not found.
    /// </summary>
    Task<Result<RecipientSnapshot>> FindAsync(string recipientId, CancellationToken cancellationToken);

    /// <summary>
    /// Degradation-aware variant of <see cref="FindAsync(string, CancellationToken)"/>:
    /// the fallback states whether the last known snapshot may answer when
    /// the local read fails.
    /// </summary>
    Task<Result<RecipientSnapshot>> FindAsync(
        string recipientId,
        RecipientReadFallback fallback,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reveals the plaintext value of one active contact point of the given
    /// recipient. The caller uses the value transiently for addressing and
    /// never persists or logs it; a removed or foreign contact point fails as
    /// not found.
    /// </summary>
    Task<Result<string>> RevealContactValueAsync(
        string recipientId,
        Guid contactPointId,
        CancellationToken cancellationToken);
}
