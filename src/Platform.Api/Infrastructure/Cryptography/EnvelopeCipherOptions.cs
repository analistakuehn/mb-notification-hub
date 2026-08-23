using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Infrastructure.Cryptography;

/// <summary>
/// Configuration of the local envelope-encryption implementation. The master
/// key only ever derives per-scope data keys; it never encrypts data
/// directly. A committed development key must carry a key id marked as
/// dev-only, because the host refuses to boot with such a key outside the
/// Development environment.
/// </summary>
public sealed class EnvelopeCipherOptions
{
    public const string SectionName = "Platform:Cryptography:Envelope";

    /// <summary>Marker that identifies a committed development key id.</summary>
    public const string DevelopmentKeyIdMarker = "dev-only";

    /// <summary>Stable identifier of the master key, stored inside every envelope.</summary>
    [Required]
    public required string KeyId { get; init; }

    /// <summary>Base64 master key with at least 32 bytes of entropy.</summary>
    [Required]
    public required string MasterKey { get; init; }
}
