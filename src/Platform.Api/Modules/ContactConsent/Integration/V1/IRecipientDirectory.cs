using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Integration.V1;

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
