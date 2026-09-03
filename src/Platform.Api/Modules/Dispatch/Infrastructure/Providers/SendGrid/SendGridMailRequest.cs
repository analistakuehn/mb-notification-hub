using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

/// <summary>
/// Wire shape of one Mail Send v3 call. The rendered HTML travels as-is:
/// this hub owns and audits its templates, so provider-side dynamic
/// templates are never used.
/// </summary>
internal sealed record SendGridMailRequest(
    [property: JsonPropertyName("personalizations")] IReadOnlyList<SendGridPersonalization> Personalizations,
    [property: JsonPropertyName("from")] SendGridAddress From,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("content")] IReadOnlyList<SendGridContent> Content,
    [property: JsonPropertyName("attachments")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SendGridAttachment>? Attachments,
    [property: JsonPropertyName("mail_settings")] SendGridMailSettings MailSettings);

internal sealed record SendGridPersonalization(
    [property: JsonPropertyName("to")] IReadOnlyList<SendGridAddress> To,
    [property: JsonPropertyName("custom_args")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? CustomArgs = null);

internal sealed record SendGridAddress(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Name);

internal sealed record SendGridContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("value")] string Value);

internal sealed record SendGridMailSettings(
    [property: JsonPropertyName("sandbox_mode")] SendGridSandboxMode SandboxMode);

internal sealed record SendGridSandboxMode(
    [property: JsonPropertyName("enable")] bool Enable);

internal sealed record SendGridErrorResponse(
    [property: JsonPropertyName("errors")] IReadOnlyList<SendGridError>? Errors);

internal sealed record SendGridError(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("field")] string? Field);

/// <summary>
/// Wire shape of one attachment inside the call. The disposition is always the
/// one that makes the provider deliver the file as a file: this hub sends what
/// it was accepted over and never rewrites a set into inline content or into a
/// link.
/// </summary>
internal sealed record SendGridAttachment(
    [property: JsonPropertyName("content")] SendGridAttachmentContent Content,
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("disposition")] string Disposition);

/// <summary>
/// The raw bytes of an attachment field, carried to the writer instead of the
/// base64 text of them.
/// <para>
/// The type exists for the call that writes it. Measured on this repository:
/// the base64 call of the JSON writer does not pass through the escape
/// encoder, so the field measures exactly four characters for every three
/// bytes, plus the quotes, under any encoder at all. Written as an ordinary
/// string value under the default encoder, the same bytes are escaped one
/// character at a time, and a sender who chooses the content chooses how long
/// the field becomes: the three byte pattern that base64 turns into the plus
/// sign alone expands sixfold, and under four megabytes of it reach the
/// ceiling of thirty million bytes the provider accepts for a whole message.
/// </para>
/// <para>
/// So the length of this field is not a question about the encoder, it is a
/// question about which write call is used, and a caller cannot choose the
/// wrong one through this type.
/// </para>
/// </summary>
[JsonConverter(typeof(SendGridAttachmentContentConverter))]
internal readonly record struct SendGridAttachmentContent(ReadOnlyMemory<byte> Raw);

/// <summary>Emits the field as base64, by the one call no content can lengthen.</summary>
internal sealed class SendGridAttachmentContentConverter : JsonConverter<SendGridAttachmentContent>
{
    public override SendGridAttachmentContent Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => new(reader.GetBytesFromBase64());

    public override void Write(
        Utf8JsonWriter writer,
        SendGridAttachmentContent value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBase64StringValue(value.Raw.Span);
    }
}
