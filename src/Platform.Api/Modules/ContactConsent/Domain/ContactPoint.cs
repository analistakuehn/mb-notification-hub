namespace NotificationHub.Api.Modules.ContactConsent.Domain;

/// <summary>
/// One addressable contact of a recipient on one channel. The value only ever
/// exists encrypted at rest plus a deterministic keyed hash for equality
/// search and uniqueness; the pair (channel, value hash) is immutable for the
/// life of the row, because consent records anchor on this row and a mutated
/// value would detach the ledger from what was actually consented to. A value
/// replacement therefore creates a new row and stamps this one removed; a
/// removed row is kept forever for the ledger and revives when the same value
/// is declared again.
/// </summary>
public sealed class ContactPoint
{
    private ContactPoint()
    {
        RecipientId = null!;
        Channel = null!;
        ValueEncrypted = null!;
        ValueHash = null!;
    }

    public Guid Id { get; private set; }

    public string RecipientId { get; private set; }

    public string Channel { get; private set; }

    /// <summary>Envelope-encrypted contact value; opens only inside this module.</summary>
    public byte[] ValueEncrypted { get; private set; }

    /// <summary>Deterministic keyed hash of the normalized value (HMAC, never a plain digest).</summary>
    public string ValueHash { get; private set; }

    public bool Verified { get; private set; }

    /// <summary>Stamped when a declaration no longer lists this value; null while the point is active.</summary>
    public DateTimeOffset? RemovedAt { get; private set; }

    public bool IsActive => RemovedAt is null;

    public static ContactPoint Declare(
        string recipientId,
        string channel,
        byte[] valueEncrypted,
        string valueHash,
        bool verified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        ArgumentNullException.ThrowIfNull(valueEncrypted);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueHash);
        if (!ContactChannels.IsCanonical(channel))
        {
            throw new ArgumentException($"Canal de contato desconhecido: '{channel}'.", nameof(channel));
        }

        return new ContactPoint
        {
            Id = Guid.CreateVersion7(),
            RecipientId = recipientId,
            Channel = channel,
            ValueEncrypted = valueEncrypted,
            ValueHash = valueHash,
            Verified = verified,
        };
    }

    /// <summary>Applies the declared verified flag; reports whether it changed.</summary>
    public bool ApplyVerified(bool verified)
    {
        if (Verified == verified)
        {
            return false;
        }

        Verified = verified;
        return true;
    }

    public void Remove(DateTimeOffset now) => RemovedAt = now;

    /// <summary>Revives a removed point when its exact value is declared again.</summary>
    public bool Restore()
    {
        if (RemovedAt is null)
        {
            return false;
        }

        RemovedAt = null;
        return true;
    }
}
