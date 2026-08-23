namespace NotificationHub.Api.Infrastructure.Cryptography;

/// <summary>
/// Provider-agnostic envelope encryption for payloads whose exposure must stay
/// contained per application: each application uses its own data key, so
/// leaking one application's key never exposes another's data. The produced
/// envelope is self-describing: it carries the envelope format version and the
/// id of the key that protected it, which is what lets a managed-KMS
/// implementation replace the local one without a data migration.
/// </summary>
public interface IEnvelopeCipher
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> under the data key of
    /// <paramref name="application"/> and returns the full envelope
    /// (format version, key id, and authenticated ciphertext).
    /// </summary>
    Task<byte[]> EncryptAsync(string application, byte[] plaintext, CancellationToken cancellationToken);

    /// <summary>
    /// Opens an envelope produced by <see cref="EncryptAsync"/> for the same
    /// <paramref name="application"/> and returns the original plaintext. A
    /// tampered envelope, a foreign application, or an unknown key id fails
    /// with a cryptographic exception; it never returns garbage.
    /// </summary>
    Task<byte[]> DecryptAsync(string application, byte[] envelope, CancellationToken cancellationToken);
}
