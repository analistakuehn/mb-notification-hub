using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Cryptography;

/// <summary>
/// Attestation signer backed by a locally configured NIST P-256 private key.
/// It exists so development runs and unit tests produce artifacts that verify
/// exactly like the managed ones: the same algorithm name, the same DER
/// signature encoding, the same self-describing shape. The managed-KMS
/// implementation replaces this class behind the same contract, and artifacts
/// signed by either verify with the archived public key alone.
/// </summary>
internal sealed class LocalKeyAttestationSigner : IAttestationSigner, IDisposable
{
    private readonly ECDsa _key;
    private readonly string _keyId;

    public LocalKeyAttestationSigner(IOptions<AttestationSignerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        AttestationSignerOptions value = options.Value;
        if (string.IsNullOrWhiteSpace(value.PrivateKey))
        {
            throw new InvalidOperationException(
                "O provedor local de assinatura exige a chave privada PKCS#8 em base64.");
        }

        _keyId = value.KeyId;
        _key = ECDsa.Create();
        try
        {
            _key.ImportPkcs8PrivateKey(Convert.FromBase64String(value.PrivateKey), out _);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            _key.Dispose();
            throw new InvalidOperationException(
                "A chave privada de assinatura configurada não é um PKCS#8 válido em base64.",
                exception);
        }

        if (_key.KeySize != 256)
        {
            _key.Dispose();
            throw new InvalidOperationException(
                "A chave de assinatura configurada precisa ser de curva NIST P-256.");
        }
    }

    public Task<AttestationSignature> SignDigestAsync(byte[] digest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (digest.Length != AttestationAlgorithms.DigestLength)
        {
            throw new ArgumentException(
                "A assinatura de atestado cobre um digest SHA-256 de 32 bytes.", nameof(digest));
        }

        // DER on purpose: the managed key-management service returns the same
        // encoding, so an artifact never reveals which side signed it.
        var signature = _key.SignHash(digest, DSASignatureFormat.Rfc3279DerSequence);
        return Task.FromResult(
            new AttestationSignature(_keyId, AttestationAlgorithms.EcdsaSha256, signature));
    }

    public Task<AttestationPublicKey> ExportPublicKeyAsync(CancellationToken cancellationToken)
        => Task.FromResult(new AttestationPublicKey(
            _keyId,
            AttestationAlgorithms.EcdsaSha256,
            _key.ExportSubjectPublicKeyInfo()));

    public void Dispose() => _key.Dispose();
}
