using System.Text.Json;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>What the ceiling answers about one metadata payload.</summary>
/// <remarks>
/// No member takes the zero value, so a caller that leaves the answer to a
/// default gets a value nothing acts on instead of silently getting the one
/// answer that admits the payload.
/// </remarks>
internal enum MetadataPayloadVerdict
{
    /// <summary>Readable, and within the ceiling.</summary>
    Admitted = 1,

    /// <summary>
    /// The payload parses but does not transcode: an escape in it names no
    /// character. The hash cannot canonicalize what cannot be read, so it is
    /// refused for what it is rather than for its size.
    /// </summary>
    Unreadable = 2,

    /// <summary>Readable, and above the ceiling.</summary>
    AboveCeiling = 3,
}

/// <summary>
/// The ceiling on the producer metadata of a request, and the single door at
/// which that metadata is both measured and read. What the ceiling bounds is
/// the idempotency payload hash, which canonicalizes metadata recursively into
/// a buffer that grows with it, once for every accepted request and again for
/// every replay resolved against a stored registration. Shape validation is
/// the only point ahead of both, so the ceiling is imposed there or nowhere.
/// <para>
/// The number is deliberately below the ceiling the catalog publishes for
/// variables, and this module owns it rather than reading that contract,
/// because metadata pays none of the costs that number bounds: it is never
/// rendered, never walked by the domain allowlist, and never handed to the
/// sandbox. It is not stored at ingestion either, so what it buys is one
/// discriminator inside the idempotency payload. Measured over the shape real
/// producers send, an object of many short keys, canonicalization costs about
/// 19 microseconds per kB and allocates about 15 times the payload. At the
/// variables ceiling that is near 6 milliseconds and 3.7 MB per hash, twice
/// the per-notification budget the variables ceiling was itself set to defend;
/// at this one it is near 0.6 milliseconds, and 32 kB still holds far more
/// context than a request has any use for.
/// </para>
/// <para>
/// The two refusals travel together and are produced by one call, because they
/// are discovered by one traversal and separating them is what let half the
/// rule close: a payload that cannot be transcoded throws where it is walked,
/// and a size check written as a question about bytes alone answers such a
/// payload by taking the caller down with it.
/// </para>
/// <para>
/// The count is exact rather than an approximation of what the hash writes:
/// sorting object keys reorders the bytes without changing how many there are.
/// How the measure itself is defined lives with the measure, in
/// <see cref="CompactJsonSize"/>; this type owns only the number.
/// </para>
/// </summary>
internal static class MetadataPayloadSize
{
    /// <summary>
    /// 32 kB, one eighth of the ceiling the catalog publishes for variables.
    /// The ratio is the point: metadata is bounded lower because it buys less,
    /// not because it costs more per byte.
    /// </summary>
    internal const int MaxBytes = 32_768;

    /// <summary>
    /// Assesses the metadata in one traversal. Absent metadata, and a JSON
    /// null, are always admitted: the hash writes neither and neither has
    /// anything to transcode.
    /// </summary>
    internal static MetadataPayloadVerdict Assess(JsonElement? metadata)
    {
        if (metadata is not { } payload
            || payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return MetadataPayloadVerdict.Admitted;
        }

        CompactJsonSize.Outcome measured = CompactJsonSize.Measure(payload);
        if (!measured.IsReadable)
        {
            return MetadataPayloadVerdict.Unreadable;
        }

        return measured.ByteCount > MaxBytes
            ? MetadataPayloadVerdict.AboveCeiling
            : MetadataPayloadVerdict.Admitted;
    }
}
