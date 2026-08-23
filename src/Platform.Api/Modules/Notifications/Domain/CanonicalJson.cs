using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// Deterministic JSON form used for hashing and for the stored variables
/// projections: compact, object keys sorted ordinally, duplicate keys
/// collapsed to the last occurrence, array order preserved, scalar tokens
/// written exactly as parsed. The writer pins
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so the escaping
/// policy never shifts with runtime defaults; that is safe because the
/// canonical form only ever feeds hashes and storage, never HTML output.
/// </summary>
internal static class CanonicalJson
{
    /// <summary>Canonical UTF-8 bytes of <paramref name="element"/>.</summary>
    internal static byte[] CanonicalBytes(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteCanonical(element, writer);
        }

        return buffer.WrittenSpan.ToArray();
    }

    internal static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
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
