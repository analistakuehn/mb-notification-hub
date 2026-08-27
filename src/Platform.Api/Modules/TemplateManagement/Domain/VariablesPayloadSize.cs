using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// The ceiling on a variables payload and the single way its size is measured.
/// Every door that hands a payload to the allowlist scan and to the sandbox
/// reads this rule, because a ceiling enforced at one door bounds nothing: the
/// same payload reaches the same walk and the same render through the others,
/// limited only by the transport's own body limit.
/// <para>
/// The measure is the compact UTF-8 form with the escaping policy pinned, not
/// the text as it arrived, and both halves of that are correctness rather than
/// taste. Indentation and <c>\uXXXX</c> escaping are the writer's choice, so
/// measuring the arriving text answers differently for the same payload at
/// ingestion, where it arrives as the producer wrote it, and at render, where
/// it arrives from the canonical bytes that were stored; two answers over one
/// payload is what accepts a request at the door and then fails it in the
/// pipeline, with no producer able to see why. And the payload is never
/// materialized to be measured: a guard against an oversized payload that
/// begins by allocating twice that payload in UTF-16 is the wrong way round.
/// </para>
/// </summary>
public static class VariablesPayloadSize
{
    /// <summary>
    /// 256 kB. The allowlist scan walks every string value of the payload at
    /// any depth, twice per notification, at the ingestion gate and again at
    /// render, at a measured cost near 5 microseconds per kB of text; this
    /// ceiling is what keeps that walk under three milliseconds of CPU per
    /// notification instead of scaling with whatever the transport accepted.
    /// It is also the number the preview endpoint already published, so one
    /// value governs the three paths and nothing admitted at ingestion can be
    /// refused later for its size.
    /// </summary>
    public const int MaxBytes = 262_144;

    /// <summary>
    /// Whether the payload is above the ceiling. An absent payload, and a JSON
    /// null, never are: neither carries a value for the scan to walk.
    /// </summary>
    public static bool ExceedsMaxBytes(JsonElement? variables)
        => variables is { } payload
            && payload.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && CompactByteCount(payload) > MaxBytes;

    /// <summary>Bytes the payload occupies in the compact UTF-8 form this measure is defined over.</summary>
    public static long CompactByteCount(JsonElement payload)
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
