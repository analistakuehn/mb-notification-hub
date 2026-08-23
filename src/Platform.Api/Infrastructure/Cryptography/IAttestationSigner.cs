namespace NotificationHub.Api.Infrastructure.Cryptography;

/// <summary>Signing algorithms an attestation may declare.</summary>
public static class AttestationAlgorithms
{
    /// <summary>
    /// ECDSA over NIST P-256 with SHA-256, signature encoded as the DER
    /// sequence of r and s. The name matches the managed-KMS algorithm
    /// specification, so the artifact reads the same whichever implementation
    /// produced it.
    /// </summary>
    public const string EcdsaSha256 = "ECDSA_SHA_256";

    /// <summary>Digest length in bytes every algorithm above signs over.</summary>
    public const int DigestLength = 32;
}

/// <summary>
/// Signature over a digest, self-describing: it carries the id of the key that
/// produced it and the algorithm that produced it. A verifier holding only the
/// artifact and the archived public key can check it without knowing which
/// implementation signed.
/// </summary>
public sealed record AttestationSignature(string KeyId, string Algorithm, byte[] Signature);

/// <summary>
/// Public half of a signing key, in the form an independent verifier needs:
/// the DER SubjectPublicKeyInfo, plus the key id and algorithm the artifacts
/// name.
/// </summary>
public sealed record AttestationPublicKey(string KeyId, string Algorithm, byte[] SubjectPublicKeyInfo);

/// <summary>
/// Provider-agnostic signer of long-term evidence: it signs a digest, never
/// the payload, so the caller stays in control of what exactly is attested and
/// the managed-KMS implementation costs one round trip per artifact. The
/// produced signature is self-describing (key id and algorithm), which is what
/// lets a managed-KMS implementation replace the local one without changing a
/// single caller or reissuing anything already signed.
/// </summary>
public interface IAttestationSigner
{
    /// <summary>Signs a digest of <see cref="AttestationAlgorithms.DigestLength"/> bytes.</summary>
    Task<AttestationSignature> SignDigestAsync(byte[] digest, CancellationToken cancellationToken);

    /// <summary>
    /// The public half of the signing key, archived next to the evidence so
    /// verification never depends on this process, on the database, or on the
    /// key provider being reachable.
    /// </summary>
    Task<AttestationPublicKey> ExportPublicKeyAsync(CancellationToken cancellationToken);
}
