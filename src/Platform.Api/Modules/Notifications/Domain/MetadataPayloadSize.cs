using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// The ceiling on the producer metadata of a request, and the single way its
/// size is measured. What the ceiling bounds is the idempotency payload hash,
/// which canonicalizes metadata recursively into a buffer that grows with it,
/// once for every accepted request and again for every replay resolved against
/// a stored registration. Shape validation is the only point ahead of both, so
/// the ceiling is imposed there or nowhere.
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
/// The measure is the compact UTF-8 form with the escaping policy pinned, and
/// it is exact rather than an approximation: sorting object keys reorders the
/// bytes the hash writes without changing how many there are. Indentation and
/// <c>\uXXXX</c> escaping are the writer's choice, so measuring the text as it
/// arrived would answer differently for one payload depending on how the
/// producer spelled it. The payload is never materialized to be measured: a
/// guard against an oversized payload that begins by allocating twice that
/// payload in UTF-16 is the wrong way round.
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
    /// Whether the metadata is above the ceiling. An absent payload, and a
    /// JSON null, never are: the hash writes neither.
    /// </summary>
    internal static bool ExceedsMaxBytes(JsonElement? metadata)
        => metadata is { } payload
            && payload.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && CompactByteCount(payload) > MaxBytes;

    /// <summary>Bytes the payload occupies in the compact UTF-8 form this measure is defined over.</summary>
    internal static long CompactByteCount(JsonElement payload)
    {
        var discarded = new DiscardedBytes();
        using var writer = new Utf8JsonWriter(discarded, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        payload.WriteTo(writer);

        // The count is the writer's own accounting, and it is only complete
        // after the flush: bytes still pending have not been handed to the
        // sink and are not committed yet.
        writer.Flush();
        return writer.BytesCommitted;
    }

    /// <summary>
    /// Sink that accepts every byte and keeps none of them. One scratch buffer
    /// answers every span the writer asks for, so measuring allocates by the
    /// largest single token of the payload and never by the payload itself.
    /// </summary>
    private sealed class DiscardedBytes : IBufferWriter<byte>
    {
        private byte[] _scratch = new byte[4096];

        public void Advance(int count)
        {
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => Fitting(sizeHint).AsMemory();

        public Span<byte> GetSpan(int sizeHint = 0) => Fitting(sizeHint).AsSpan();

        private byte[] Fitting(int sizeHint)
        {
            if (sizeHint > _scratch.Length)
            {
                _scratch = new byte[sizeHint];
            }

            return _scratch;
        }
    }
}
