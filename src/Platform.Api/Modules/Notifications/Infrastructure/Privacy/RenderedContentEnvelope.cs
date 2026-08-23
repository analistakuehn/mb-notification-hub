using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

/// <summary>
/// Sole owner of the sealed shape of an attempt's rendered content, and of its
/// two phases. While the attempt still has to be sent, the envelope carries
/// the complete content at the top level, which is what the dispatcher opens,
/// and the masked content beside it in a member of its own. Once the send has
/// a terminal verdict the complete form has no remaining purpose: the envelope
/// is rewritten with the masked content at the top level and no companion
/// member, so what stays at rest proves that a value was sent and never which
/// one. A render whose two forms coincide never carries the companion member,
/// so its envelope is already durable the moment it is written and no
/// transition ever rewrites it.
/// </summary>
internal static class RenderedContentEnvelope
{
    private const string ChannelMember = "channel";
    private const string LocaleMember = "locale";
    private const string SubjectMember = "subject";
    private const string BodyMember = "body";
    private const string BodyTextMember = "bodyText";

    /// <summary>Companion member holding the masked form while the complete one is still needed.</summary>
    private const string MaskedMember = "masked";

    /// <summary>
    /// Seals one render: the complete form as the content to send and, when
    /// the masked form differs from it, the masked form beside it.
    /// </summary>
    public static async Task<byte[]> SealAsync(
        IEnvelopeCipher cipher,
        string application,
        PublishedTemplateRender render,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(render);
        RenderedForm full = render.Full;
        JsonObject content = Content(
            render.Channel, render.ResolvedLocale, full.Subject, full.Body, full.BodyText);

        // Only a masked form that actually differs earns the companion member.
        // With identical forms the complete content already is the masked one,
        // and a later transition would rewrite the row for nothing.
        if (render.Masked is { } masked
            && !string.Equals(masked.ContentHash, full.ContentHash, StringComparison.Ordinal))
        {
            content[MaskedMember] = new JsonObject
            {
                [SubjectMember] = masked.Subject,
                [BodyMember] = masked.Body,
                [BodyTextMember] = masked.BodyText,
            };
        }

        return await EncryptAsync(cipher, application, content, cancellationToken);
    }

    /// <summary>
    /// Seals one already-masked form as the durable content of an attempt: the
    /// same shape a transition leaves behind, built from a fresh render
    /// instead of from a companion member.
    /// </summary>
    public static async Task<byte[]> SealMaskedAsync(
        IEnvelopeCipher cipher,
        string application,
        string channel,
        string locale,
        RenderedForm masked,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(masked);
        return await EncryptAsync(
            cipher,
            application,
            Content(channel, locale, masked.Subject, masked.Body, masked.BodyText),
            cancellationToken);
    }

    /// <summary>
    /// Rewrites one sealed envelope with its masked form alone and returns the
    /// new envelope. Returns null when the envelope carries no companion
    /// member, which means there is nothing to discard and the stored bytes
    /// stay exactly as they are.
    /// </summary>
    public static async Task<byte[]?> TryDiscardCompleteFormAsync(
        IEnvelopeCipher cipher,
        string application,
        byte[] sealedContent,
        CancellationToken cancellationToken)
    {
        JsonObject envelope = await OpenAsync(cipher, application, sealedContent, cancellationToken);
        if (envelope[MaskedMember] is not JsonObject masked)
        {
            return null;
        }

        // Removing the companion first keeps the member order of a freshly
        // sealed envelope: assigning an existing key updates it in place.
        envelope.Remove(MaskedMember);
        envelope[SubjectMember] = masked[SubjectMember]?.DeepClone();
        envelope[BodyMember] = masked[BodyMember]?.DeepClone();
        envelope[BodyTextMember] = masked[BodyTextMember]?.DeepClone();
        return await EncryptAsync(cipher, application, envelope, cancellationToken);
    }

    /// <summary>Opens one envelope and reads the content it carries.</summary>
    public static async Task<SealedRenderedContent> ReadAsync(
        IEnvelopeCipher cipher,
        string application,
        byte[] sealedContent,
        CancellationToken cancellationToken)
    {
        JsonObject envelope = await OpenAsync(cipher, application, sealedContent, cancellationToken);
        return new SealedRenderedContent(
            Text(envelope, ChannelMember)
                ?? throw new InvalidOperationException("O conteúdo selado não declara o canal."),
            Text(envelope, LocaleMember)
                ?? throw new InvalidOperationException("O conteúdo selado não declara o locale."),
            Text(envelope, SubjectMember),
            Text(envelope, BodyMember)
                ?? throw new InvalidOperationException("O conteúdo selado não carrega o corpo."),
            Text(envelope, BodyTextMember),
            envelope[MaskedMember] is JsonObject);
    }

    /// <summary>
    /// Opens one envelope and reads the masked form it carries, wherever that
    /// form currently sits: the companion member while the complete form is
    /// still needed for the send, the top-level content once the terminal
    /// verdict discarded the complete one. A disclosure reads through here, so
    /// no caller has to know which phase the envelope is in and none of them can
    /// reach the complete form by mistake.
    /// </summary>
    public static async Task<SealedRenderedContent> ReadMaskedAsync(
        IEnvelopeCipher cipher,
        string application,
        byte[] sealedContent,
        CancellationToken cancellationToken)
    {
        JsonObject envelope = await OpenAsync(cipher, application, sealedContent, cancellationToken);
        var channel = Text(envelope, ChannelMember)
            ?? throw new InvalidOperationException("O conteúdo selado não declara o canal.");
        var locale = Text(envelope, LocaleMember)
            ?? throw new InvalidOperationException("O conteúdo selado não declara o locale.");

        JsonObject form = envelope[MaskedMember] as JsonObject ?? envelope;
        return new SealedRenderedContent(
            channel,
            locale,
            Text(form, SubjectMember),
            Text(form, BodyMember)
                ?? throw new InvalidOperationException("O conteúdo selado não carrega o corpo."),
            Text(form, BodyTextMember),
            envelope[MaskedMember] is JsonObject);
    }

    private static JsonObject Content(
        string channel,
        string locale,
        string? subject,
        string body,
        string? bodyText)
        => new()
        {
            [ChannelMember] = channel,
            [LocaleMember] = locale,
            [SubjectMember] = subject,
            [BodyMember] = body,
            [BodyTextMember] = bodyText,
        };

    private static async Task<JsonObject> OpenAsync(
        IEnvelopeCipher cipher,
        string application,
        byte[] sealedContent,
        CancellationToken cancellationToken)
    {
        var plaintext = await cipher.DecryptAsync(application, sealedContent, cancellationToken);
        return JsonNode.Parse(plaintext.AsSpan()) as JsonObject
            ?? throw new InvalidOperationException("O conteúdo selado do attempt não é um objeto JSON.");
    }

    private static async Task<byte[]> EncryptAsync(
        IEnvelopeCipher cipher,
        string application,
        JsonObject content,
        CancellationToken cancellationToken)
        => await cipher.EncryptAsync(
            application, Encoding.UTF8.GetBytes(content.ToJsonString()), cancellationToken);

    private static string? Text(JsonObject envelope, string member)
    {
        JsonNode? node = envelope[member];
        return node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
    }
}

/// <summary>
/// The content one sealed envelope carries, opened in memory. The plaintext
/// lives here for the duration of one operation and never reaches a log, a
/// queue or another store.
/// </summary>
internal sealed record SealedRenderedContent(
    string Channel,
    string Locale,
    string? Subject,
    string Body,
    string? BodyText,
    bool CarriesMaskedForm);
