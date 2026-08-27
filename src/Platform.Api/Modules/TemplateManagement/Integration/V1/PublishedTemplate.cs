namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// Reasons the published catalog reports for a template identity that no
/// longer accepts new notification requests. The values belong to the
/// canonical rejection-reason catalog shared with the notification events, so
/// a consumer forwards them without translation.
/// </summary>
public static class TemplateRejectionReasons
{
    public const string Deprecated = "template-deprecated";
    public const string Disabled = "template-disabled";
}

/// <summary>
/// Reasons the published render refuses to frame a message with the layout a
/// version pins. The values belong to the canonical rejection-reason catalog
/// shared with the notification events, so a consumer forwards them without
/// translation.
/// </summary>
public static class LayoutRejectionReasons
{
    /// <summary>
    /// The layout identity is disabled, so nothing frames a message with it
    /// any more. Every template that pins it is refused, whatever its class:
    /// rendering the body without the wrapper would ship content whose
    /// canonical hash matches nothing that was ever approved, and a layout is
    /// disabled precisely when the wrapper itself is what must stop going out.
    /// </summary>
    public const string Disabled = "layout-disabled";
}

/// <summary>
/// Decision metadata of the published version of a template: everything a
/// sibling module needs to validate, route and audit a notification request
/// without touching this module's internals.
/// </summary>
public sealed record PublishedTemplate
{
    public required string Application { get; init; }

    public required string TemplateKey { get; init; }

    /// <summary>Canonical class value: critical, transactional or operational.</summary>
    public required string Class { get; init; }

    public required string OwnerTeam { get; init; }

    public required string Purpose { get; init; }

    public required string LegalBasis { get; init; }

    /// <summary>Variable names whose values only ever leave this module masked on the stored render form.</summary>
    public required IReadOnlyList<string> SensitiveVariables { get; init; }

    /// <summary>Channels the published version ships content for.</summary>
    public required IReadOnlyList<Channel> ChannelsWithContent { get; init; }

    /// <summary>Locale the content fallback chain lands on, when the template declares one.</summary>
    public string? DefaultLocale { get; init; }

    /// <summary>Number of the published version.</summary>
    public required int Version { get; init; }

    /// <summary>Canonical content hash of the published version, the value its approval vouches for.</summary>
    public required string ContentHash { get; init; }
}

/// <summary>
/// Outcome of a published-template lookup: either the decision metadata of the
/// published version, or the catalog reason the consumer must reject new
/// requests with. The rejection is data, not an error, because rejecting is
/// the consumer's own legitimate flow.
/// </summary>
public abstract record PublishedTemplateLookup
{
    private PublishedTemplateLookup()
    {
    }

    /// <summary>The template is active and has a published version.</summary>
    public sealed record Published(PublishedTemplate Template) : PublishedTemplateLookup;

    /// <summary>
    /// The template rejects new requests; <see cref="Reason"/> carries the
    /// catalog value from <see cref="TemplateRejectionReasons"/>.
    /// </summary>
    public sealed record Rejected(string Reason) : PublishedTemplateLookup;
}
