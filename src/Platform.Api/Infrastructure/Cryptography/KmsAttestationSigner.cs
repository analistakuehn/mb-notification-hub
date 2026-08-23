using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Cryptography;

/// <summary>
/// Attestation signer delegating to the managed key-management service. The
/// process never holds private key material: it sends the digest and receives
/// the signature, and the archived public key is fetched from the same
/// service. Signing over the digest (never the payload) keeps the request
/// small and constant whatever the size of the evidence being attested.
/// </summary>
internal sealed class KmsAttestationSigner(
    IAmazonKeyManagementService kms,
    IOptions<AttestationSignerOptions> options) : IAttestationSigner
{
    public async Task<AttestationSignature> SignDigestAsync(byte[] digest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (digest.Length != AttestationAlgorithms.DigestLength)
        {
            throw new ArgumentException(
                "A assinatura de atestado cobre um digest SHA-256 de 32 bytes.", nameof(digest));
        }

        using var message = new MemoryStream(digest, writable: false);
        SignResponse response = await kms.SignAsync(
            new SignRequest
            {
                KeyId = options.Value.KeyId,
                Message = message,
                MessageType = MessageType.DIGEST,
                SigningAlgorithm = SigningAlgorithmSpec.ECDSA_SHA_256,
            },
            cancellationToken);

        return new AttestationSignature(
            options.Value.KeyId,
            AttestationAlgorithms.EcdsaSha256,
            response.Signature.ToArray());
    }

    public async Task<AttestationPublicKey> ExportPublicKeyAsync(CancellationToken cancellationToken)
    {
        GetPublicKeyResponse response = await kms.GetPublicKeyAsync(
            new GetPublicKeyRequest { KeyId = options.Value.KeyId },
            cancellationToken);

        return new AttestationPublicKey(
            options.Value.KeyId,
            AttestationAlgorithms.EcdsaSha256,
            response.PublicKey.ToArray());
    }
}
