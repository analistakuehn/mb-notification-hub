using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Deterministic JSON form used for hashing: compact, object keys sorted
/// ordinally, duplicate keys collapsed to the last occurrence. The stored
/// column is plain text, so the submitted bytes survive the round trip and the
/// canonical form recomputes identically after a reload. The writer pins
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so the escaping
/// policy never shifts with runtime defaults; that is safe because the
/// canonical form only ever feeds the content hash and is never emitted as
/// HTML.
/// </summary>
internal static class CanonicalJson
{
    internal static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteCanonical(document.RootElement, writer);
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
