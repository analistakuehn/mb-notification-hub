using System.Security.Cryptography;

namespace NotificationHub.Api.Infrastructure.Cryptography;

/// <summary>
/// Verification half of the attestation contract: a pure function of the
/// archived public key, the digest, and the signature. It deliberately takes
/// no signer, no configuration, and no key provider, so an auditor holding
/// only the archived artifacts can run exactly the check the platform runs.
/// </summary>
public static class AttestationVerification
{
    /// <summary>
    /// True when <paramref name="signature"/> is a valid signature of
    /// <paramref name="digest"/> under <paramref name="publicKey"/>. An
    /// unknown algorithm, a malformed key, or a malformed signature returns
    /// false; it never throws for untrusted input, because a verifier fed a
    /// forged artifact must report failure, not crash.
    /// </summary>
    public static bool VerifyDigest(AttestationPublicKey publicKey, byte[] digest, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(signature);

        if (!string.Equals(publicKey.Algorithm, AttestationAlgorithms.EcdsaSha256, StringComparison.Ordinal)
            || digest.Length != AttestationAlgorithms.DigestLength)
        {
            return false;
        }

        try
        {
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey.SubjectPublicKeyInfo, out _);
            return ecdsa.VerifyHash(digest, signature, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
