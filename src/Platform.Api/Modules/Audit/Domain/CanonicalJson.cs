using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NotificationHub.Api.Modules.Audit.Domain;

/// <summary>
/// The one canonicalization of this module: compact UTF-8 JSON with object
/// keys in ordinal order. The hash chain hashes it and the export manifest is
/// written with it, so an auditor learns a single serialization rule and can
/// reproduce every hash in the evidence with it.
/// </summary>
internal static class CanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Canonical text of an arbitrary JSON document.</summary>
    internal static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Write(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Creates a writer over <paramref name="buffer"/> with the canonical options.</summary>
    internal static Utf8JsonWriter CreateWriter(ArrayBufferWriter<byte> buffer)
        => new(buffer, WriterOptions);

    /// <summary>
    /// Writes <paramref name="element"/> canonically into an open writer:
    /// objects with their keys sorted ordinally, arrays in their original
    /// order, scalars as the parser read them.
    /// </summary>
    internal static void Write(JsonElement element, Utf8JsonWriter writer)
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
                    Write(value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
