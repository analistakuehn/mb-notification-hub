using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotificationHub.PerformanceTests.ProviderTransfer;

/// <summary>
/// Wire shape of one Mail Send v3 call, attachments included. The e-mail
/// adapter of the hub composes the same members for a message without
/// attachments; this copy exists because the probe runs outside the module
/// that owns the adapter and must not reach into it. Whether the two shapes
/// still agree is asserted by a test, not by this comment.
/// </summary>
internal sealed record MailSendRequest(
    [property: JsonPropertyName("personalizations")] IReadOnlyList<MailSendPersonalization> Personalizations,
    [property: JsonPropertyName("from")] MailSendAddress From,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("content")] IReadOnlyList<MailSendContent> Content,
    [property: JsonPropertyName("attachments")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<MailSendAttachment>? Attachments,
    [property: JsonPropertyName("mail_settings")] MailSendSettings MailSettings);

internal sealed record MailSendPersonalization(
    [property: JsonPropertyName("to")] IReadOnlyList<MailSendAddress> To,
    [property: JsonPropertyName("custom_args")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? CustomArgs = null);

internal sealed record MailSendAddress(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Name);

internal sealed record MailSendContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("value")] string Value);

internal sealed record MailSendAttachment(
    [property: JsonPropertyName("content")] AttachmentContent Content,
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("disposition")] string Disposition,
    [property: JsonPropertyName("content_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ContentId = null);

internal sealed record MailSendSettings(
    [property: JsonPropertyName("sandbox_mode")] MailSendSandbox SandboxMode);

internal sealed record MailSendSandbox(
    [property: JsonPropertyName("enable")] bool Enable);

/// <summary>
/// The raw bytes of one attachment, carried to the writer instead of the
/// base64 text of them. The field is emitted by the writer call that encodes
/// the bytes itself, and that call is the whole point: a base64 alphabet
/// written as ordinary text is escaped character by character by the default
/// JSON encoder, so a sender who chooses the content chooses how long the
/// field becomes. The pattern of three bytes that base64 turns into the plus
/// sign alone expands eightfold and reaches the provider ceiling with under
/// four megabytes of content. Encoding at the writer removes the choice.
/// </summary>
[JsonConverter(typeof(AttachmentContentConverter))]
internal readonly record struct AttachmentContent(ReadOnlyMemory<byte> Raw)
{
    internal long RawBytes => Raw.Length;
}

/// <summary>Emits the attachment as base64, by the one call no content can lengthen.</summary>
internal sealed class AttachmentContentConverter : JsonConverter<AttachmentContent>
{
    public override AttachmentContent Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => new(reader.GetBytesFromBase64());

    public override void Write(
        Utf8JsonWriter writer,
        AttachmentContent value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBase64StringValue(value.Raw.Span);
    }
}

/// <summary>
/// Ceilings the provider documents for one Mail Send call, and the arithmetic
/// that turns raw bytes into the base64 they occupy inside the JSON body.
/// </summary>
internal static class MailSendLimits
{
    /// <summary>Total message size the provider accepts, headers and body included.</summary>
    internal const long MaxMessageBytes = 30_000_000;

    /// <summary>Per-attachment size the provider recommends staying under.</summary>
    internal const long RecommendedAttachmentBytes = 10_000_000;

    /// <summary>Room kept for recipients, headers and custom arguments, 100 KiB.</summary>
    internal const long EnvelopeReserveBytes = 102_400;

    internal const string AttachmentDisposition = "attachment";

    /// <summary>
    /// Raw content of one message under the conservative reading: what is left
    /// of the message ceiling after the envelope reserve, taken back through
    /// the base64 expansion of four bytes for every three.
    /// </summary>
    internal const long MaxRawContentBytes = (MaxMessageBytes - EnvelopeReserveBytes) * 3 / 4;

    /// <summary>Base64 expands three raw bytes into four characters, padded.</summary>
    internal static long Base64Length(long rawBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawBytes);
        return checked(4 * ((rawBytes + 2) / 3));
    }
}

/// <summary>
/// Everything of one message except the attachment bytes. The same instance
/// composes the request every arm sends, so a difference between arms cannot
/// come from a difference of envelope.
/// </summary>
internal sealed record MailSendEnvelope(
    string Recipient,
    string SenderEmail,
    string? SenderName,
    string Subject,
    string TextBody,
    string HtmlBody,
    bool SandboxMode)
{
    internal static MailSendEnvelope Default { get; } = new(
        "destinatario@example.com",
        "no-reply@example.com",
        "Notification Hub",
        "Confirmação de operação",
        "Segue o comprovante em anexo.",
        "<p>Segue o comprovante em anexo.</p>",
        true);

    internal MailSendRequest Compose(IReadOnlyList<MailSendAttachment>? attachments)
        => new(
            [new MailSendPersonalization([new MailSendAddress(Recipient, null)])],
            new MailSendAddress(SenderEmail, string.IsNullOrWhiteSpace(SenderName) ? null : SenderName),
            Subject,
            // text/plain before text/html: Mail Send v3 requires content
            // ordered by ascending preference.
            [
                new MailSendContent("text/plain", TextBody),
                new MailSendContent("text/html", HtmlBody),
            ],
            attachments,
            new MailSendSettings(new MailSendSandbox(SandboxMode)));
}

/// <summary>
/// One body split into the literal parts and the attachment slots between
/// them. An arm that never materializes the message writes segment, content,
/// segment, content, segment; an arm that materializes it serializes the
/// request outright. Both are the same bytes, and nothing but the provider
/// double is entitled to say so.
/// </summary>
internal sealed record MailSendBodyLayout(
    IReadOnlyList<byte[]> Segments,
    IReadOnlyList<long> ContentBase64Lengths)
{
    internal long TotalBytes
        => Segments.Sum(segment => (long)segment.Length) + ContentBase64Lengths.Sum();

    /// <summary>Bytes of the message that are not attachment content.</summary>
    internal long EnvelopeBytes => Segments.Sum(segment => (long)segment.Length);
}

/// <summary>Serializes the request, and derives the streaming layout from it.</summary>
internal static class MailSendComposer
{
    internal const string Path = "/v3/mail/send";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    internal static byte[] Serialize(MailSendRequest request)
        => JsonSerializer.SerializeToUtf8Bytes(request, Options);

    /// <summary>
    /// Serializes the request once with a placeholder in each attachment, then
    /// cuts the bytes at the placeholders. The cut is what guarantees that the
    /// incremental arms emit the identical envelope around the content, so the
    /// only thing left for the digest comparison to catch is the content path
    /// itself.
    /// <para>
    /// The placeholder is raw bytes, like the content it stands in for, so the
    /// needle to look for is the base64 of it. Cutting on the encoded form is
    /// what keeps the cut honest under any writer call: a call that expanded
    /// the field would not leave the needle in the body, and the layout would
    /// refuse instead of silently describing a message nobody sends.
    /// </para>
    /// </summary>
    internal static MailSendBodyLayout Layout(
        MailSendEnvelope envelope,
        IReadOnlyList<IAttachmentByteSource> sources,
        string markerPrefix)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPrefix);

        var needles = new byte[sources.Count][];
        var attachments = new MailSendAttachment[sources.Count];
        for (var index = 0; index < sources.Count; index++)
        {
            var marker = Encoding.UTF8.GetBytes($"{markerPrefix}-{index}");
            needles[index] = Encoding.UTF8.GetBytes(Convert.ToBase64String(marker));
            attachments[index] = new MailSendAttachment(
                new AttachmentContent(marker),
                sources[index].FileName,
                sources[index].ContentType,
                MailSendLimits.AttachmentDisposition);
        }

        var body = Serialize(envelope.Compose(attachments));
        var segments = new List<byte[]>(sources.Count + 1);
        var offset = 0;
        foreach (var needle in needles)
        {
            var found = body.AsSpan(offset).IndexOf(needle);
            if (found < 0)
            {
                throw new InvalidOperationException(
                    "O marcador do anexo não apareceu no corpo serializado na forma codificada; "
                    + "o recorte do layout não é confiável.");
            }

            segments.Add(body[offset..(offset + found)]);
            offset += found + needle.Length;
        }

        segments.Add(body[offset..]);
        return new MailSendBodyLayout(
            segments,
            [.. sources.Select(source => MailSendLimits.Base64Length(source.Length))]);
    }
}
