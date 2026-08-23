using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Cryptography;

namespace NotificationHub.UnitTests.Infrastructure;

/// <summary>
/// The signer contract as a verifier sees it. The artifact says which key
/// signed it and with which algorithm, which is what allows the managed
/// implementation to replace the local one without reissuing anything.
/// </summary>
public sealed class AttestationSignerTests
{
    private static readonly byte[] Digest = SHA256.HashData("manifesto"u8.ToArray());

    [Fact]
    public async Task A_signature_verifies_against_the_exported_public_key()
    {
        using LocalKeyAttestationSigner signer = CreateSigner();

        AttestationSignature signature = await signer.SignDigestAsync(Digest, CancellationToken.None);
        AttestationPublicKey publicKey = await signer.ExportPublicKeyAsync(CancellationToken.None);

        signature.Algorithm.ShouldBe(AttestationAlgorithms.EcdsaSha256);
        signature.KeyId.ShouldBe("attestation-tests-dev-only");
        publicKey.KeyId.ShouldBe(signature.KeyId);
        AttestationVerification.VerifyDigest(publicKey, Digest, signature.Signature).ShouldBeTrue();
    }

    [Fact]
    public async Task A_signature_does_not_verify_over_a_different_digest()
    {
        using LocalKeyAttestationSigner signer = CreateSigner();
        AttestationSignature signature = await signer.SignDigestAsync(Digest, CancellationToken.None);
        AttestationPublicKey publicKey = await signer.ExportPublicKeyAsync(CancellationToken.None);

        var other = SHA256.HashData("outro manifesto"u8.ToArray());

        AttestationVerification.VerifyDigest(publicKey, other, signature.Signature).ShouldBeFalse();
    }

    [Fact]
    public async Task A_malformed_signature_is_reported_as_invalid_instead_of_throwing()
    {
        using LocalKeyAttestationSigner signer = CreateSigner();
        AttestationPublicKey publicKey = await signer.ExportPublicKeyAsync(CancellationToken.None);

        AttestationVerification.VerifyDigest(publicKey, Digest, [1, 2, 3]).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_algorithm_never_verifies()
    {
        using LocalKeyAttestationSigner signer = CreateSigner();
        AttestationSignature signature = await signer.SignDigestAsync(Digest, CancellationToken.None);
        AttestationPublicKey publicKey = await signer.ExportPublicKeyAsync(CancellationToken.None);

        AttestationVerification
            .VerifyDigest(publicKey with { Algorithm = "RSASSA_PSS_SHA_256" }, Digest, signature.Signature)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Signing_covers_a_digest_and_refuses_anything_else()
    {
        using LocalKeyAttestationSigner signer = CreateSigner();

        await Should.ThrowAsync<ArgumentException>(
            () => signer.SignDigestAsync("conteúdo inteiro"u8.ToArray(), CancellationToken.None));
    }

    [Fact]
    public void The_local_provider_refuses_to_start_without_its_private_key()
    {
        Should.Throw<InvalidOperationException>(() => new LocalKeyAttestationSigner(
            Options.Create(new AttestationSignerOptions { KeyId = "sem-chave" })));
    }

    [Fact]
    public void The_local_provider_refuses_a_key_that_is_not_the_declared_curve()
    {
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var pkcs8 = Convert.ToBase64String(other.ExportPkcs8PrivateKey());

        Should.Throw<InvalidOperationException>(() => new LocalKeyAttestationSigner(
            Options.Create(new AttestationSignerOptions { KeyId = "curva-errada", PrivateKey = pkcs8 })));
    }

    private static LocalKeyAttestationSigner CreateSigner()
        => new(Options.Create(new AttestationSignerOptions
        {
            KeyId = "attestation-tests-dev-only",
            PrivateKey = TestPrivateKey,
        }));

    /// <summary>Fixed NIST P-256 pair; it only ever signs test artifacts.</summary>
    private const string TestPrivateKey =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgJlTwSPSOMZJSqkeUexDqKEIo5nh2"
        + "oWJcKTZ5YCXT8NehRANCAASoZWiwNFThfecCCgQQJEWJnYXoJKE0QGnBSFM2XytFGdRYMAJsB8Sn"
        + "D1n6NpjUMecTKs0TKHJ1qCgNVdQa2bV8";
}
