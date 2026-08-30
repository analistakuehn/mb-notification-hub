using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>What one attempt at a canonical form found.</summary>
/// <remarks>
/// No member takes the zero value, so a caller that leaves the answer to a
/// default gets a value nothing acts on instead of silently getting the one
/// answer that admits the document.
/// </remarks>
internal enum CanonicalJsonVerdict
{
    /// <summary>Readable, and a JSON object, which is what every governed document is.</summary>
    Canonical = 1,

    /// <summary>Not JSON at all.</summary>
    Malformed = 2,

    /// <summary>
    /// The document parses but does not transcode: an escape in it names no
    /// character. Nothing downstream can read it, so no hash over it means
    /// anything, and it is refused for what it is rather than for its content.
    /// </summary>
    Unreadable = 3,

    /// <summary>
    /// Readable, and legal JSON that is not an object. It carries a canonical
    /// form all the same, because a value inside a document is canonicalized by
    /// the same walk; only a document governed as an object refuses it.
    /// </summary>
    NotAnObject = 4,
}

/// <summary>
/// One canonical form and the verdict the same traversal reached about it. The
/// admitted state is carried by the verdict rather than by its negation, so the
/// default value of the struct reads as refused: a caller that lets an
/// uninitialized one through refuses the document instead of admitting it as an
/// empty one.
/// </summary>
/// <param name="Verdict">What the traversal found.</param>
/// <param name="Text">
/// The canonical form, present whenever the document could be read at all and
/// absent otherwise. It is present for <see cref="CanonicalJsonVerdict.NotAnObject"/>
/// on purpose: the shape rule and the readability rule are two findings of one
/// walk, and a caller canonicalizing a value rather than a governed document
/// wants the form and not the shape.
/// </param>
internal readonly record struct CanonicalJsonForm(CanonicalJsonVerdict Verdict, string? Text);

/// <summary>
/// Deterministic JSON form used for hashing: compact, object keys sorted
/// ordinally, duplicate keys collapsed to the last occurrence. The stored
/// column is plain text, so the submitted bytes survive the round trip and the
/// canonical form recomputes identically after a reload. The writer pins
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so the escaping
/// policy never shifts with runtime defaults; that is safe because the
/// canonical form only ever feeds the content hash and is never emitted as
/// HTML.
/// <para>
/// Not every document that parses can be written back out. An escape may name
/// a surrogate the document never pairs, which is legal JSON text: the reader
/// accepts it and it binds without complaint, and only the transcoding to UTF-8
/// discovers that the escape names no character. That is a property of the
/// document, not a fault of this walk, so it is returned as an answer instead
/// of thrown. A caller has to be able to refuse such a document at its own
/// door, the same way it refuses a malformed one, rather than take a runtime
/// exception through whatever transport it was serving at the time.
/// </para>
/// </summary>
internal static class CanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Reads <paramref name="json"/> and produces its canonical form in one
    /// traversal, which is also the traversal that discovers whether it can be
    /// read and whether it is an object. The three answers come from one walk
    /// so that no caller can obtain one of them and act as if it had them all,
    /// and so the guard costs no traversal the hash was not already paying for.
    /// </summary>
    internal static CanonicalJsonForm TryNormalize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var canonical = NormalizeReadable(document.RootElement);
            return new CanonicalJsonForm(
                document.RootElement.ValueKind == JsonValueKind.Object
                    ? CanonicalJsonVerdict.Canonical
                    : CanonicalJsonVerdict.NotAnObject,
                canonical);
        }
        catch (JsonException)
        {
            // Not JSON. Distinct from the case below because the author who
            // typed it gets told which of the two is wrong.
            return new CanonicalJsonForm(CanonicalJsonVerdict.Malformed, null);
        }
        catch (InvalidOperationException)
        {
            // The exact type the transcoding raises when an escape names a
            // surrogate the document never pairs, in either of the two forms it
            // reports. Nothing here decides that by inspecting the text: the
            // runtime already owns the rule, and a scanner of our own would be
            // a second reading of it that can disagree with the one that
            // actually transcodes.
            //
            // The two catches name those types and no wider one on purpose.
            // ArgumentException in particular stays out: the writer raises it
            // when a string handed to it is already invalid UTF-16 in memory,
            // which no stored column and no request body can produce, only a
            // caller inside this process that built one. Catching it here would
            // report a broken caller as an unreadable document, which is how a
            // measure stops being able to fail.
            return new CanonicalJsonForm(CanonicalJsonVerdict.Unreadable, null);
        }
    }

    /// <summary>
    /// Canonical form of an element whose readability the caller has already
    /// established, by measuring the root it came from. It bypasses the verdict
    /// on purpose and must never receive an element that was not measured: the
    /// transcoding throws on one that was not, which is the caller's defect and
    /// not this document's property.
    /// </summary>
    internal static string NormalizeReadable(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteCanonical(element, writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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
