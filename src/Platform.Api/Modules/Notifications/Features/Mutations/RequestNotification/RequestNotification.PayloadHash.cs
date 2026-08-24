using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Features.Mutations;

internal static partial class RequestNotification
{
    /// <summary>
    /// SHA-256 (lowercase hex) of the canonical form of the request body,
    /// computed over the bound command so transport noise never changes the
    /// hash. Canonical form: one compact JSON object whose members appear in
    /// fixed ordinal order (<c>application</c>, <c>channelsHint</c>,
    /// <c>class</c>, <c>correlationId</c>, <c>metadata</c>,
    /// <c>recipientId</c>, <c>scheduledAt</c>, <c>templateKey</c>,
    /// <c>ttlSeconds</c>, <c>variables</c>); absent optional members are
    /// omitted, and a JSON-null <c>variables</c> or <c>metadata</c> counts as
    /// absent; <c>variables</c> and <c>metadata</c> are canonicalized
    /// recursively with object keys in ordinal order and scalar tokens written
    /// exactly as parsed; <c>channelsHint</c> keeps its order because it is a
    /// preference; <c>scheduledAt</c> is normalized to UTC in the round-trip
    /// format. Two bodies differing only in property order, whitespace, or
    /// time-zone offset of the same instant therefore hash identically.
    ///
    /// <c>locale</c> is deliberately absent. It reaches no decision of the
    /// hub, so two requests that differ only in it are the same notification;
    /// hashing it would answer a retry that corrected the field, or a client
    /// library that filled its default differently between the attempt and the
    /// retry, with a conflict instead of the replay the producer is owed.
    /// </summary>
    internal static string ComputePayloadHash(Command command)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("application", command.Application);
            if (command.ChannelsHint is not null)
            {
                writer.WriteStartArray("channelsHint");
                foreach (var channel in command.ChannelsHint)
                {
                    writer.WriteStringValue(channel);
                }

                writer.WriteEndArray();
            }

            writer.WriteString("class", command.Class);
            if (command.CorrelationId is not null)
            {
                writer.WriteString("correlationId", command.CorrelationId);
            }

            if (command.Metadata is { ValueKind: JsonValueKind.Object } metadata)
            {
                writer.WritePropertyName("metadata");
                CanonicalJson.WriteCanonical(metadata, writer);
            }

            writer.WriteString("recipientId", command.RecipientId);
            if (command.ScheduledAt is { } scheduledAt)
            {
                writer.WriteString(
                    "scheduledAt",
                    scheduledAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            }

            writer.WriteString("templateKey", command.TemplateKey);
            writer.WriteNumber("ttlSeconds", command.TtlSeconds);
            if (command.Variables is { ValueKind: JsonValueKind.Object } variables)
            {
                writer.WritePropertyName("variables");
                CanonicalJson.WriteCanonical(variables, writer);
            }

            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }
}
