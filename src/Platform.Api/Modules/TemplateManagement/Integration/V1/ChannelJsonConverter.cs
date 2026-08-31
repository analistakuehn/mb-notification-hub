using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// Round trip of the canonical channel vocabulary. The wire form is the
/// canonical word, the same one every consumer already stores and compares,
/// and reading one back resolves to the instance the closed set holds instead
/// of building a second object with the same value.
/// <para>
/// Without it the closed set writes itself as a wrapper object and does not
/// read back at all: the type has no parameterless and no public parameterized
/// constructor, so deserializing one throws. That is why consumers ended up
/// projecting the word themselves on the way in and on the way out.
/// </para>
/// <para>
/// It is internal on purpose. Publishing it would add a type to the surface
/// this module exposes, and no consumer ever needs to name it: the attribute
/// on the vocabulary applies it wherever the type is serialized, inside this
/// assembly and outside it alike.
/// </para>
/// </summary>
internal sealed class ChannelJsonConverter : JsonConverter<Channel>
{
    public override Channel Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"A channel reads from a string, and the document carried {reader.TokenType}.");
        }

        var word = reader.GetString();
        Result<Channel> channel = Channel.Create(word);
        return channel.IsSuccess
            ? channel.Value!
            : throw new JsonException($"Unknown channel '{word}'.");
    }

    public override void Write(Utf8JsonWriter writer, Channel value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.Value);
    }
}
