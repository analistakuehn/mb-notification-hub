using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NotificationHub.PerformanceTests.Infrastructure;

/// <summary>
/// One month-long partition of the trail: the name, the range boundaries and
/// the advisory-lock key that serializes appends inside it.
/// </summary>
internal sealed record PartitionMonth(string Name, DateTimeOffset FromInclusive, DateTimeOffset ToExclusive, long LockKey)
{
    internal static PartitionMonth Of(DateTimeOffset instant)
    {
        DateTime utc = instant.UtcDateTime;
        var from = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return new PartitionMonth(
            $"audit_event_{from.Year:D4}_{from.Month:D2}",
            from,
            from.AddMonths(1),
            AuditChainArithmetic.PartitionLockKey(from.Year, from.Month));
    }

    /// <summary>An instant inside the month, spread by the offset given.</summary>
    internal DateTimeOffset InstantAt(long offset)
    {
        TimeSpan span = ToExclusive - FromInclusive;
        return FromInclusive + TimeSpan.FromTicks(Math.Abs(offset) % (span.Ticks - TimeSpan.TicksPerMinute));
    }
}

/// <summary>One trail entry as the probe writes it, before the chain columns exist.</summary>
internal sealed record TrailEntry(
    Guid Id,
    DateTimeOffset OccurredAt,
    string ActorType,
    string ActorId,
    string? Application,
    string Action,
    string EntityType,
    string EntityId,
    string DetailsJson);

/// <summary>
/// The probe's own copy of the chain arithmetic. The production types are
/// internal to the API assembly and this project is deliberately outside its
/// friend list: widening production visibility to run a measurement would be a
/// production change, and this slice must not make one. The shapes below
/// mirror the appender byte for byte (field order, timestamp precision, anchor
/// preimage, lock keyspace) so the rows the probe writes cost what production
/// rows cost and replay under the same rule.
/// </summary>
internal static class AuditChainArithmetic
{
    private const string AnchorPreimagePrefix = "notification-hub:audit-chain:";

    private const string AnchorPreimageSuffix = ":anchor";

    private const long LockKeyScope = 0x41554449;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static long PartitionLockKey(int year, int month)
        => (LockKeyScope << 32) | (uint)((year * 100) + month);

    internal static byte[] PartitionAnchor(string partitionName)
        => SHA256.HashData(Encoding.UTF8.GetBytes(
            AnchorPreimagePrefix + partitionName + AnchorPreimageSuffix));

    internal static byte[] Link(byte[] prevHash, string canonical)
    {
        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
        var material = new byte[prevHash.Length + canonicalBytes.Length];
        prevHash.CopyTo(material, 0);
        canonicalBytes.CopyTo(material, prevHash.Length);
        return SHA256.HashData(material);
    }

    internal static string CanonicalDocument(TrailEntry entry, long seq)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var details = JsonDocument.Parse(entry.DetailsJson);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
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
            WriteCanonical(details.RootElement, writer);
            writer.WriteString("entityId", entry.EntityId);
            writer.WriteString("entityType", entry.EntityType);
            writer.WriteString("id", entry.Id.ToString("D"));
            writer.WriteString(
                "occurredAt",
                TruncateToMicroseconds(entry.OccurredAt).UtcDateTime
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteNumber("seq", seq);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), TimeSpan.Zero);
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    properties[property.Name] = property.Value;
                }

                foreach ((var name, JsonElement value) in properties)
                {
                    writer.WritePropertyName(name);
                    WriteCanonical(value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
