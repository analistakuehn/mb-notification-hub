using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Infrastructure.Cryptography;

/// <summary>
/// Configuration of the attestation signer. The provider selects the
/// implementation without changing any caller: the local one signs with a
/// configured private key and suits development and unit tests, the managed
/// one delegates to the key-management service and never exposes private key
/// material to the process. A committed development key must carry a key id
/// marked as dev-only, because the host refuses to boot with such a key
/// outside the Development environment.
/// </summary>
public sealed class AttestationSignerOptions
{
    public const string SectionName = "Platform:Cryptography:Attestation";

    /// <summary>Signs with a private key held in configuration.</summary>
    public const string LocalProvider = "local";

    /// <summary>Signs through the managed key-management service.</summary>
    public const string KmsProvider = "kms";

    /// <summary>Marker that identifies a committed development key id.</summary>
    public const string DevelopmentKeyIdMarker = "dev-only";

    [Required]
    public string Provider { get; init; } = LocalProvider;

    /// <summary>
    /// Stable identifier of the signing key, written into every attestation
    /// and into the name of the archived public key. For the managed provider
    /// it is the key id or key ARN the service resolves.
    /// </summary>
    [Required]
    public required string KeyId { get; init; }

    /// <summary>
    /// Base64 PKCS#8 private key of a NIST P-256 pair. Required by the local
    /// provider, ignored by the managed one, which never sees private key
    /// material.
    /// </summary>
    public string? PrivateKey { get; init; }

    /// <summary>Custom endpoint of the key-management service; null uses the AWS default.</summary>
    public string? ServiceUrl { get; init; }

    public string? Region { get; init; }

    public string? AccessKey { get; init; }

    public string? SecretKey { get; init; }

    /// <summary>True when the provider name is one this platform composes.</summary>
    public bool HasKnownProvider()
        => string.Equals(Provider, LocalProvider, StringComparison.Ordinal)
            || string.Equals(Provider, KmsProvider, StringComparison.Ordinal);

    /// <summary>True when the local provider has the private key it needs.</summary>
    public bool HasProviderMaterial()
        => !string.Equals(Provider, LocalProvider, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(PrivateKey);
}
