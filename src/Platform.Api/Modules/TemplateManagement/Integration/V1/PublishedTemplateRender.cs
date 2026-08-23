using System.Text.Json;

namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>What to render from the published catalog and with which variables.</summary>
public sealed record PublishedRenderRequest
{
    public required string Application { get; init; }

    public required string TemplateKey { get; init; }

    /// <summary>Canonical channel value: email, sms, push or whatsapp.</summary>
    public required string Channel { get; init; }

    /// <summary>Requested locale; content resolution follows the fallback chain.</summary>
    public required string Locale { get; init; }

    /// <summary>Variables payload; values are data for the sandbox, never template source.</summary>
    public JsonElement? Variables { get; init; }

    /// <summary>
    /// When true the response also carries the masked form: the same render
    /// repeated with every sensitive variable masked, which is the only form a
    /// trail may store.
    /// </summary>
    public bool IncludeMaskedForm { get; init; }
}

/// <summary>
/// One rendered form of the resolved content entry, with the canonical hash
/// computed over exactly these three fields.
/// </summary>
public sealed record RenderedForm(string? Subject, string Body, string? BodyText, string ContentHash);

/// <summary>
/// Render of the published version for one channel and locale, with the
/// pinned layout applied. The full form goes to dispatch and its hash is
/// computed before any masking; the masked form is what a trail stores and
/// hashes what was stored. Without a sensitive variable in the payload the two
/// forms coincide.
/// </summary>
public sealed record PublishedTemplateRender
{
    public required string Channel { get; init; }

    public required string RequestedLocale { get; init; }

    /// <summary>Locale the fallback chain landed on.</summary>
    public required string ResolvedLocale { get; init; }

    /// <summary>Published version number the render used.</summary>
    public required int Version { get; init; }

    /// <summary>Complete content for dispatch.</summary>
    public required RenderedForm Full { get; init; }

    /// <summary>Masked content for storage and trail, when the caller asked for it.</summary>
    public RenderedForm? Masked { get; init; }
}
