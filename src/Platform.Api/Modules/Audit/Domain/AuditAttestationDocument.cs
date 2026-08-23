using System.Buffers;
using System.Text.Json;
using NotificationHub.Api.Infrastructure.Cryptography;

namespace NotificationHub.Api.Modules.Audit.Domain;

/// <summary>
/// The signature stored next to a manifest. It names the key and the
/// algorithm, so an auditor who finds the file years later knows exactly what
/// to verify and with which archived key, without asking the platform.
/// </summary>
internal sealed record AuditAttestationDocument(
    string Algorithm,
    string KeyId,
    string ManifestDigest,
    byte[] Signature)
{
    public byte[] CanonicalBytes()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = CanonicalJson.CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("algorithm", Algorithm);
            writer.WriteString("keyId", KeyId);
            writer.WriteString("manifestDigest", ManifestDigest);
            writer.WriteString("signature", Convert.ToBase64String(Signature));
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static AuditAttestationDocument Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using JsonDocument document = JsonDocument.Parse(content);
        JsonElement root = document.RootElement;
        return new AuditAttestationDocument(
            root.GetProperty("algorithm").GetString()!,
            root.GetProperty("keyId").GetString()!,
            root.GetProperty("manifestDigest").GetString()!,
            Convert.FromBase64String(root.GetProperty("signature").GetString()!));
    }
}

/// <summary>
/// The archived public half of a signing key. Verification of decade-old
/// evidence must not depend on the key-management service still resolving the
/// key, so the public key travels into the same immutable bucket as the
/// evidence it vouches for.
/// </summary>
internal sealed record AuditAttestationKeyDocument(string Algorithm, string KeyId, byte[] PublicKey)
{
    public byte[] CanonicalBytes()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = CanonicalJson.CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("algorithm", Algorithm);
            writer.WriteString("keyId", KeyId);
            writer.WriteString("publicKey", Convert.ToBase64String(PublicKey));
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static AuditAttestationKeyDocument Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using JsonDocument document = JsonDocument.Parse(content);
        JsonElement root = document.RootElement;
        return new AuditAttestationKeyDocument(
            root.GetProperty("algorithm").GetString()!,
            root.GetProperty("keyId").GetString()!,
            Convert.FromBase64String(root.GetProperty("publicKey").GetString()!));
    }

    /// <summary>The archived key in the shape the platform verifier consumes.</summary>
    public AttestationPublicKey ToPublicKey() => new(KeyId, Algorithm, PublicKey);
}
