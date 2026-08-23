using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Cryptography;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;

/// <summary>The two persisted forms of one contact value.</summary>
internal sealed record ProtectedContactValue(byte[] Encrypted, string Hash);

/// <summary>
/// Produces the stored forms of a contact value and opens them again, all
/// inside this module. Encryption uses the platform envelope cipher under the
/// module's dedicated key scope, so contact data never shares a data key with
/// any application payload. The equality hash is an HMAC-SHA256 under a key
/// derived from the platform master key with a module-owned label, distinct
/// from the cipher's data-key derivation: deterministic for equality search
/// and uniqueness, yet useless to anyone without the master key, which is what
/// a plain digest of a low-entropy phone or e-mail could never guarantee.
/// </summary>
internal sealed class ContactValueProtector
{
    /// <summary>Envelope-cipher key scope dedicated to this module's contact data.</summary>
    internal const string KeyScope = "contact-consent";

    private const int HashKeySize = 32;

    private readonly IEnvelopeCipher _cipher;
    private readonly byte[] _hashKey;

    public ContactValueProtector(IEnvelopeCipher cipher, IOptions<EnvelopeCipherOptions> options)
    {
        _cipher = cipher;
        _hashKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Convert.FromBase64String(options.Value.MasterKey),
            HashKeySize,
            salt: null,
            info: Encoding.UTF8.GetBytes("contact-consent:value-hash"));
    }

    public async Task<ProtectedContactValue> ProtectAsync(
        string normalizedValue,
        CancellationToken cancellationToken)
    {
        var encrypted = await _cipher.EncryptAsync(
            KeyScope, Encoding.UTF8.GetBytes(normalizedValue), cancellationToken);
        return new ProtectedContactValue(encrypted, Hash(normalizedValue));
    }

    public string Hash(string normalizedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedValue);
        return Convert.ToHexStringLower(
            HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes(normalizedValue)));
    }

    public async Task<string> RevealAsync(byte[] encrypted, CancellationToken cancellationToken)
        => Encoding.UTF8.GetString(await _cipher.DecryptAsync(KeyScope, encrypted, cancellationToken));
}
