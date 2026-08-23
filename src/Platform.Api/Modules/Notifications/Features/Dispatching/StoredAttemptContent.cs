using System.Text.Json;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.Dispatching;

/// <summary>
/// The sealed render of one attempt, opened in memory at send time only. The
/// shape mirrors exactly what the render stage sealed; the plaintext lives in
/// this record for the duration of one send and never reaches a log, a queue
/// or another store.
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
        var plaintext = await cipher.DecryptAsync(application, renderedContentEncrypted, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(plaintext);
        JsonElement root = document.RootElement;
        return new StoredAttemptContent(
            root.GetProperty("channel").GetString()
                ?? throw new InvalidOperationException("O conteúdo selado não declara o canal."),
            ReadOptional(root, "subject"),
            root.GetProperty("body").GetString()
                ?? throw new InvalidOperationException("O conteúdo selado não carrega o corpo."),
            ReadOptional(root, "bodyText"));
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

    private static string? ReadOptional(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
