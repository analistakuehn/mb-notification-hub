using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NotificationHub.Api.Modules.Audit.Domain;

/// <summary>
/// Backward link of one manifest to the export that precedes it. Following the
/// links is what turns a bucket of independent daily files into a chain: a
/// missing day stops being an absence nobody notices and becomes a reference
/// that does not resolve.
/// </summary>
internal sealed record AuditExportManifestLink(string Key, string Partition, string TailHash);

/// <summary>
/// The manifest of one export: everything an auditor needs to check the
/// evidence without the database, and nothing that changes between two runs
/// over the same data (no clock, no host, no run id), because a rerun must
/// produce the same bytes it produced the first time.
/// </summary>
/// <remarks>
/// The authoritative claim is the sequence range, not the calendar window: the
/// window names the day that triggered the export, while
/// <see cref="SeqMin"/>..<see cref="SeqMax"/> delimits the contiguous chain
/// segment the file carries. Keeping the segment contiguous is what allows
/// <c>hash = SHA-256(prev_hash ‖ canonical)</c> to be replayed from
/// <see cref="HeadPrevHash"/> to <see cref="TailHash"/> without exporting a
/// single hash per line.
/// </remarks>
internal sealed record AuditExportManifest
{
    internal const int CurrentFormatVersion = 1;

    internal const string DailyType = AuditExportKeys.DailyExport;

    internal const string ClosingType = AuditExportKeys.ClosingExport;

    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    public required int FormatVersion { get; init; }

    public required string Table { get; init; }

    public required string Partition { get; init; }

    /// <summary>Daily slice or the authoritative closing export of the partition.</summary>
    public required string Type { get; init; }

    public required DateTimeOffset WindowFrom { get; init; }

    public required DateTimeOffset WindowTo { get; init; }

    /// <summary>First sequence of the exported segment; zero when it carries no chained row.</summary>
    public required long SeqMin { get; init; }

    /// <summary>Last sequence of the exported segment; zero when it carries no chained row.</summary>
    public required long SeqMax { get; init; }

    public required int ChainedCount { get; init; }

    public required int UnchainedCount { get; init; }

    /// <summary>Deterministic partition anchor, hex; a verifier rebuilds it from the partition name alone.</summary>
    public required string Anchor { get; init; }

    /// <summary>Chain state the segment starts from, hex; equals the anchor for the first segment of the partition.</summary>
    public required string HeadPrevHash { get; init; }

    /// <summary>Chain state the segment ends at, hex; equals the head when the segment carries no chained row.</summary>
    public required string TailHash { get; init; }

    /// <summary>SHA-256 of the decompressed events stream, hex.</summary>
    public required string UncompressedDigest { get; init; }

    /// <summary>SHA-256 of the stored compressed events object, hex.</summary>
    public required string CompressedDigest { get; init; }

    /// <summary>SHA-256 of the stored compressed pre-chain object, hex; absent when there is none.</summary>
    public string? UnchainedDigest { get; init; }

    public AuditExportManifestLink? Previous { get; init; }

    /// <summary>The exact bytes that are stored and attested.</summary>
    public byte[] CanonicalBytes()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = CanonicalJson.CreateWriter(buffer))
        {
            // Property names in ordinal order; keep them sorted when a field
            // joins the manifest.
            writer.WriteStartObject();
            writer.WriteString("anchor", Anchor);
            writer.WriteNumber("chainedCount", ChainedCount);
            writer.WriteString("compressedDigest", CompressedDigest);
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("headPrevHash", HeadPrevHash);
            writer.WriteString("partition", Partition);
            if (Previous is null)
            {
                writer.WriteNull("previous");
            }
            else
            {
                writer.WriteStartObject("previous");
                writer.WriteString("key", Previous.Key);
                writer.WriteString("partition", Previous.Partition);
                writer.WriteString("tailHash", Previous.TailHash);
                writer.WriteEndObject();
            }

            writer.WriteNumber("seqMax", SeqMax);
            writer.WriteNumber("seqMin", SeqMin);
            writer.WriteString("table", Table);
            writer.WriteString("tailHash", TailHash);
            writer.WriteString("type", Type);
            writer.WriteString("uncompressedDigest", UncompressedDigest);
            writer.WriteNumber("unchainedCount", UnchainedCount);
            if (UnchainedDigest is null)
            {
                writer.WriteNull("unchainedDigest");
            }
            else
            {
                writer.WriteString("unchainedDigest", UnchainedDigest);
            }

            writer.WriteString("windowFrom", FormatInstant(WindowFrom));
            writer.WriteString("windowTo", FormatInstant(WindowTo));
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads a manifest back from its stored bytes.</summary>
    public static AuditExportManifest Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using JsonDocument document = JsonDocument.Parse(content);
        JsonElement root = document.RootElement;
        JsonElement previous = root.GetProperty("previous");

        return new AuditExportManifest
        {
            FormatVersion = root.GetProperty("formatVersion").GetInt32(),
            Table = root.GetProperty("table").GetString()!,
            Partition = root.GetProperty("partition").GetString()!,
            Type = root.GetProperty("type").GetString()!,
            WindowFrom = ParseInstant(root.GetProperty("windowFrom").GetString()!),
            WindowTo = ParseInstant(root.GetProperty("windowTo").GetString()!),
            SeqMin = root.GetProperty("seqMin").GetInt64(),
            SeqMax = root.GetProperty("seqMax").GetInt64(),
            ChainedCount = root.GetProperty("chainedCount").GetInt32(),
            UnchainedCount = root.GetProperty("unchainedCount").GetInt32(),
            Anchor = root.GetProperty("anchor").GetString()!,
            HeadPrevHash = root.GetProperty("headPrevHash").GetString()!,
            TailHash = root.GetProperty("tailHash").GetString()!,
            UncompressedDigest = root.GetProperty("uncompressedDigest").GetString()!,
            CompressedDigest = root.GetProperty("compressedDigest").GetString()!,
            UnchainedDigest = root.GetProperty("unchainedDigest").GetString(),
            Previous = previous.ValueKind == JsonValueKind.Null
                ? null
                : new AuditExportManifestLink(
                    previous.GetProperty("key").GetString()!,
                    previous.GetProperty("partition").GetString()!,
                    previous.GetProperty("tailHash").GetString()!),
        };
    }

    private static string FormatInstant(DateTimeOffset value)
        => value.ToUniversalTime().UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseInstant(string value)
        => DateTimeOffset.ParseExact(
            value,
            InstantFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}

/// <summary>Encoding helpers shared by the exporter and the verifiers.</summary>
internal static class AuditHex
{
    internal static string ToHex(byte[] value) => Convert.ToHexStringLower(value);

    internal static byte[] FromHex(string value) => Convert.FromHexString(value);

    internal static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
