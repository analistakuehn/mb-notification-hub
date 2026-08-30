using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NotificationHub.SharedKernel;

/// <summary>
/// The one way a JSON payload is measured, and the one place that answers
/// whether it can be measured at all. Two modules impose different ceilings on
/// different payloads; each owns its number and neither owns the mechanism,
/// because a second copy of this walk is a second place for the answer to
/// drift and for a defect in it to survive the fix applied to the first.
/// <para>
/// The measure is the compact UTF-8 form with the escaping policy pinned, not
/// the text as it arrived. Indentation and <c>\uXXXX</c> escaping are the
/// writer's choice, so measuring the arriving text answers differently for one
/// payload depending on how its producer spelled it. The payload is never
/// materialized to be measured: a guard against an oversized payload that
/// begins by allocating twice that payload in UTF-16 is the wrong way round.
/// </para>
/// <para>
/// Not every payload that parses can be written back out. An escape may name a
/// surrogate the payload never pairs, which is legal JSON text: the reader
/// accepts it and it binds to a <see cref="JsonElement"/> without complaint,
/// and only the transcoding to UTF-8 discovers that the escape names no
/// character. That is a property of the payload, not a fault of this walk, so
/// it is returned as an answer instead of thrown. A caller has to be able to
/// refuse such a payload at its own door, the same way it refuses an oversized
/// one, rather than take a runtime exception through whatever transport it was
/// serving at the time.
/// </para>
/// </summary>
public static class CompactJsonSize
{
    // Pinned so the escaping policy never shifts with runtime defaults, and so
    // the count never depends on which encoder the caller happened to hold.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Measures <paramref name="payload"/> in a single traversal, which is
    /// also the traversal that discovers whether it can be read: the two
    /// answers come from one walk so that no caller can obtain one of them
    /// and act as if it had both.
    /// </summary>
    public static Outcome Measure(JsonElement payload)
    {
        try
        {
            var discarded = new DiscardedBytes();
            using var writer = new Utf8JsonWriter(discarded, WriterOptions);

            payload.WriteTo(writer);

            // The count is the writer's own accounting, and it is only
            // complete after the flush: bytes still pending have not been
            // handed to the sink and are not committed yet.
            writer.Flush();
            return new Outcome(true, writer.BytesCommitted);
        }
        catch (InvalidOperationException)
        {
            // The exact type the transcoding raises when an escape names a
            // surrogate the payload never pairs, in either of the two forms it
            // reports: a high surrogate with no low one after it, and a
            // surrogate value that is invalid where it sits. Nothing here
            // decides that by inspecting the text: the runtime already owns
            // the rule, and a scanner of our own would be a second reading of
            // it that can disagree with the one that actually transcodes.
            //
            // The catch names that type and no wider one on purpose. Catching
            // every exception would also swallow a defect in this walk and
            // report the payload as the unreadable thing, which is how a
            // measure stops being able to fail.
            return new Outcome(false, 0);
        }
    }

    /// <summary>
    /// What one measurement found. The readable state is carried rather than
    /// its negation, so the default value of the struct reads as unreadable: a
    /// caller that lets an uninitialized one through refuses the payload
    /// instead of admitting it as an empty one.
    /// </summary>
    /// <param name="IsReadable">Whether the payload transcodes to UTF-8 at all.</param>
    /// <param name="ByteCount">Bytes of the compact form, meaningful only when it is readable.</param>
    public readonly record struct Outcome(bool IsReadable, long ByteCount);

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
