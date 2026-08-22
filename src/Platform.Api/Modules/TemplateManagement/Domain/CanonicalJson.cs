using System.Buffers;
using System.Text;
using System.Text.Json;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Deterministic JSON form used for hashing: compact, object keys sorted
/// ordinally, duplicate keys collapsed to the last occurrence. The stored
/// column is jsonb, which rewrites whitespace and key order on round trip;
/// hashing the canonical form keeps <c>content_hash</c> stable across reloads.
/// </summary>
internal static class CanonicalJson
{
    internal static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
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

                foreach ((string name, JsonElement value) in properties)
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
