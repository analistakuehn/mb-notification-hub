namespace NotificationHub.Api.Infrastructure.Cryptography;

/// <summary>
/// Provider-agnostic envelope encryption for payloads whose exposure must stay
/// contained per key scope: each scope (an application, a module-owned data
/// class) uses its own data key, so leaking one scope's key never exposes
/// another's data. The produced envelope is self-describing: it carries the
/// envelope format version and the id of the key that protected it, which is
/// what lets a managed-KMS implementation replace the local one without a data
/// migration.
/// </summary>
public interface IEnvelopeCipher
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> under the data key of
    /// <paramref name="keyScope"/> and returns the full envelope
    /// (format version, key id, and authenticated ciphertext).
    /// </summary>
    Task<byte[]> EncryptAsync(string keyScope, byte[] plaintext, CancellationToken cancellationToken);

    /// <summary>
    /// Opens an envelope produced by <see cref="EncryptAsync"/> for the same
    /// <paramref name="keyScope"/> and returns the original plaintext. A
    /// tampered envelope, a foreign key scope, or an unknown key id fails
    /// with a cryptographic exception; it never returns garbage.
    /// </summary>
    Task<byte[]> DecryptAsync(string keyScope, byte[] envelope, CancellationToken cancellationToken);
}
