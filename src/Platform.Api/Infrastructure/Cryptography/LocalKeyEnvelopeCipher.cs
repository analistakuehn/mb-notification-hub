using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Cryptography;

/// <summary>
/// Envelope encryption backed by a locally configured master key: the data key
/// of each scope is derived with HKDF-SHA256 from the master key using the key
/// scope as info, and the payload is sealed with AES-256-GCM. The key id and
/// the key scope are bound as associated data, so an envelope only ever opens
/// for the scope and key that produced it. A managed-KMS implementation
/// replaces this class behind the same contract.
/// </summary>
/// <remarks>
/// Envelope layout, version 1:
/// <c>[version:1][keyIdLength:1][keyId:utf8][nonce:12][tag:16][ciphertext]</c>.
/// </remarks>
internal sealed class LocalKeyEnvelopeCipher : IEnvelopeCipher
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DataKeySize = 32;

    private readonly byte[] _masterKey;
    private readonly byte[] _keyIdBytes;
    private readonly string _keyId;

    public LocalKeyEnvelopeCipher(IOptions<EnvelopeCipherOptions> options)
    {
        _masterKey = Convert.FromBase64String(options.Value.MasterKey);
        if (_masterKey.Length < DataKeySize)
        {
            throw new InvalidOperationException(
                "A chave-mestra da cifra de envelope precisa de pelo menos 32 bytes.");
        }

        _keyId = options.Value.KeyId;
        _keyIdBytes = Encoding.UTF8.GetBytes(_keyId);
        if (_keyIdBytes.Length is 0 or > byte.MaxValue)
        {
            throw new InvalidOperationException(
                "O identificador da chave de envelope deve ter entre 1 e 255 bytes em UTF-8.");
        }
    }

    public Task<byte[]> EncryptAsync(string keyScope, byte[] plaintext, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyScope);
        ArgumentNullException.ThrowIfNull(plaintext);

        var envelope = new byte[2 + _keyIdBytes.Length + NonceSize + TagSize + plaintext.Length];
        envelope[0] = FormatVersion;
        envelope[1] = (byte)_keyIdBytes.Length;
        _keyIdBytes.CopyTo(envelope, 2);

        Span<byte> nonce = envelope.AsSpan(2 + _keyIdBytes.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        Span<byte> tag = envelope.AsSpan(2 + _keyIdBytes.Length + NonceSize, TagSize);
        Span<byte> ciphertext = envelope.AsSpan(2 + _keyIdBytes.Length + NonceSize + TagSize);

        using var aes = new AesGcm(DeriveDataKey(keyScope), TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(keyScope));
        return Task.FromResult(envelope);
    }

    public Task<byte[]> DecryptAsync(string keyScope, byte[] envelope, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyScope);
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Length < 2 || envelope[0] != FormatVersion)
        {
            throw new CryptographicException("Envelope com versão de formato desconhecida.");
        }

        int keyIdLength = envelope[1];
        var headerLength = 2 + keyIdLength + NonceSize + TagSize;
        if (envelope.Length < headerLength)
        {
            throw new CryptographicException("Envelope truncado.");
        }

        var keyId = Encoding.UTF8.GetString(envelope, 2, keyIdLength);
        if (!string.Equals(keyId, _keyId, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                $"O envelope foi protegido pela chave '{keyId}', desconhecida desta configuração.");
        }

        ReadOnlySpan<byte> nonce = envelope.AsSpan(2 + keyIdLength, NonceSize);
        ReadOnlySpan<byte> tag = envelope.AsSpan(2 + keyIdLength + NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = envelope.AsSpan(headerLength);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(DeriveDataKey(keyScope), TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(keyScope));
        return Task.FromResult(plaintext);
    }

    private byte[] DeriveDataKey(string keyScope)
        => HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            _masterKey,
            DataKeySize,
            salt: null,
            info: Encoding.UTF8.GetBytes($"envelope-data-key:{keyScope}"));

    private byte[] AssociatedData(string keyScope)
        => Encoding.UTF8.GetBytes($"{_keyId}:{keyScope}");
}
