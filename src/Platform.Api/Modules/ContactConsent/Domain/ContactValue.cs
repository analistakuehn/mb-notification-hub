namespace NotificationHub.Api.Modules.ContactConsent.Domain;

/// <summary>
/// Canonical form of a contact value before encryption and hashing. The same
/// normalization runs on every write and on every equality search, so the
/// deterministic hash of a value only ever has one spelling: trimming for all
/// channels, plus lowercasing for e-mail addresses, whose local-part case
/// differences are treated as the same mailbox by every real-world provider.
/// </summary>
public static class ContactValue
{
    public static string Normalize(string channel, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        return channel == ContactChannels.Email
            ? trimmed.ToLowerInvariant()
            : trimmed;
    }
}
