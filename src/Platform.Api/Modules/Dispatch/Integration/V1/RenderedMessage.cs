namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Rendered content of one delivery attempt, discriminated by channel. The
/// hierarchy is closed inside this module: a new channel arrives as a new
/// record here, never as a foreign subtype, so every adapter can exhaust the
/// shapes it accepts. Content arrives final from the render stage; adapters
/// never rewrite it, because the audited content hash must describe the exact
/// bytes handed to the provider.
/// </summary>
public abstract record RenderedMessage
{
    private protected RenderedMessage()
    {
    }
}

/// <summary>
/// Rendered e-mail content. The preheader is part of the rendered shape even
/// though providers have no dedicated field for it: embedding it into the
/// HTML belongs to the render stage, not to an adapter.
/// </summary>
public sealed record EmailMessage(
    string Subject,
    string Preheader,
    string HtmlBody,
    string TextBody) : RenderedMessage;

/// <summary>Rendered push content: visible notification plus the data payload the app consumes.</summary>
public sealed record PushMessage(
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> DataPayload) : RenderedMessage;
