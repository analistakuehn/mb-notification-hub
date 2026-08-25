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
/// plaintext egress is an explicit, searchable call site. The suppression
/// ledger reads through the snapshot too: it joined this contract as a new
/// member instead of a V2 surface, so one resolution answers reachability and
/// suppression together.
/// </summary>
public interface IRecipientDirectory
{
    /// <summary>
    /// Resolves the full snapshot of one recipient: profile with the default
    /// timezone applied, active contact points, current consent per purpose
    /// and channel, active device tokens and the suppressions in force. An
    /// unknown recipient fails as not found.
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

    /// <summary>
    /// Reveals the routing token of one active device registration of the
    /// given recipient, for transient use at send time only. An invalidated
    /// or foreign registration fails as not found, which is the caller's
    /// signal to fail the attempt instead of sending.
    /// </summary>
    Task<Result<string>> RevealDeviceTokenAsync(
        string recipientId,
        Guid deviceTokenId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Masks a set of contact points of one recipient in a single read. The
    /// masking rule runs inside this module over the decrypted value, so the
    /// plaintext never crosses the boundary and no caller can reimplement the
    /// rule from a value it should not hold. The read is by set on purpose: a
    /// consumer showing the targets of several delivery attempts asks once
    /// instead of once per attempt.
    /// </summary>
    /// <remarks>
    /// Deliberate opening: this read also answers for a contact point already
    /// stamped removed, marking it inactive, because a historical consumer
    /// asks where a message went, not where a message would go now. Unknown
    /// ids and ids of another recipient are simply absent from the answer,
    /// which keeps the read from confirming the existence of anything.
    /// </remarks>
    Task<Result<IReadOnlyList<MaskedContactPoint>>> MaskContactPointsAsync(
        string recipientId,
        IReadOnlyCollection<Guid> contactPointIds,
        CancellationToken cancellationToken);
}
