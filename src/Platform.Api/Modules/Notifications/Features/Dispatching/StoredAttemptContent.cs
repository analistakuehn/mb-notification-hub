using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

namespace NotificationHub.Api.Modules.Notifications.Features.Dispatching;

/// <summary>
/// The sealed render of one attempt, opened in memory at send time only. The
/// content to send is the top level of the envelope, which is the complete
/// form until the verdict discards it; the plaintext lives in this record for
/// the duration of one send and never reaches a log, a queue or another store.
/// </summary>
internal sealed record StoredAttemptContent(
    string Channel,
    string? Subject,
    string Body,
    string? BodyText)
{
    private static readonly IReadOnlyDictionary<string, string> EmptyData =
        new Dictionary<string, string>();

    /// <summary>Opens the envelope of one attempt with the application's data key.</summary>
    public static async Task<StoredAttemptContent> OpenAsync(
        IEnvelopeCipher cipher,
        string application,
        byte[] renderedContentEncrypted,
        CancellationToken cancellationToken)
    {
        SealedRenderedContent content = await RenderedContentEnvelope.ReadAsync(
            cipher, application, renderedContentEncrypted, cancellationToken);
        return new StoredAttemptContent(
            content.Channel, content.Subject, content.Body, content.BodyText);
    }

    /// <summary>
    /// Projects the opened content into the published rendered-message shape
    /// of the channel. The preheader travels empty on purpose: embedding it
    /// into the HTML belongs to the render stage, and the audited hashes
    /// describe the sealed bytes as they are.
    /// </summary>
    public RenderedMessage ToRenderedMessage()
        => Channel switch
        {
            "email" => new EmailMessage(Subject ?? "", "", Body, BodyText ?? ""),
            "push" => new PushMessage(Subject ?? "", Body, EmptyData),
            _ => throw new InvalidOperationException(
                $"O canal '{Channel}' não possui projeção de conteúdo nesta fase."),
        };
}
