using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Domain;

/// <summary>
/// Pure arithmetic of the per-partition hash chain. Each monthly partition of
/// the audit table carries its own chain: every chained event stores the exact
/// canonical bytes that were hashed, the hash of its predecessor in the same
/// partition, and <c>SHA-256(prev_hash ‖ canonical)</c>. The first chained
/// event of a partition links to that partition's deterministic anchor, so a
/// verifier can recompute the whole chain from the stored columns alone; rows
/// older than the chain stay unchained and are never rewritten.
/// </summary>
internal static class AuditChain
{
    /// <summary>
    /// Documented preimage of the partition anchor:
    /// <c>notification-hub:audit-chain:{partition}:anchor</c>. Any verifier can
    /// rebuild the anchor from the partition name alone; the fixed prefix and
    /// suffix keep the preimage outside the value space of real event links.
    /// </summary>
    private const string AnchorPreimagePrefix = "notification-hub:audit-chain:";

    private const string AnchorPreimageSuffix = ":anchor";

    /// <summary>
    /// High half of every advisory-lock key taken by the chain ("AUDI" in
    /// ASCII), scoping the chain's keyspace away from any other advisory-lock
    /// user of the same database.
    /// </summary>
    private const long LockKeyScope = 0x41554449;

    /// <summary>
    /// Canonical JSON document of one event: compact, UTF-8, object keys in
    /// ordinal order, details embedded as canonicalized JSON. The text is
    /// stored next to the hashes, so verification replays these exact bytes
    /// and never depends on how the store reserializes the details column.
    /// </summary>
    internal static string CanonicalDocument(Guid id, long seq, AuditEntry entry)
    {
        using var details = JsonDocument.Parse(entry.DetailsJson);
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = CanonicalJson.CreateWriter(buffer))
        {
            // Property names are already in ordinal order; keep them sorted
            // when a field joins the vocabulary.
            writer.WriteStartObject();
            writer.WriteString("action", entry.Action);
            writer.WriteString("actorId", entry.ActorId);
            writer.WriteString("actorType", entry.ActorType);
            if (entry.Application is null)
            {
                writer.WriteNull("application");
            }
            else
            {
                writer.WriteString("application", entry.Application);
            }

            writer.WritePropertyName("details");
            CanonicalJson.Write(details.RootElement, writer);
            writer.WriteString("entityId", entry.EntityId);
            writer.WriteString("entityType", entry.EntityType);
            writer.WriteString("id", id.ToString("D"));
            writer.WriteString(
                "occurredAt",
                TruncateToMicroseconds(entry.OccurredAt).UtcDateTime
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteNumber("seq", seq);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Hash of one chain link: SHA-256 over the predecessor hash followed by the canonical UTF-8 bytes.</summary>
    internal static byte[] Link(byte[] prevHash, string canonical)
    {
        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
        var material = new byte[prevHash.Length + canonicalBytes.Length];
        prevHash.CopyTo(material, 0);
        canonicalBytes.CopyTo(material, prevHash.Length);
        return SHA256.HashData(material);
    }

    /// <summary>Deterministic anchor a partition's first chained event links to.</summary>
    internal static byte[] PartitionAnchor(string partitionName)
        => SHA256.HashData(Encoding.UTF8.GetBytes(
            AnchorPreimagePrefix + partitionName + AnchorPreimageSuffix));

    /// <summary>Advisory-lock key serializing chain appends within one monthly partition.</summary>
    internal static long PartitionLockKey(int year, int month)
        => (LockKeyScope << 32) | (uint)(year * 100 + month);

    /// <summary>
    /// The store keeps timestamptz at microsecond precision; truncating makes
    /// the canonical text and the stored column describe the same instant.
    /// </summary>
    internal static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % (TimeSpan.TicksPerMillisecond / 1000), TimeSpan.Zero);
    }
}
