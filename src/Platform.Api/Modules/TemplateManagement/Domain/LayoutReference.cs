using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>One (channel, locale) coordinate of a content entry.</summary>
public sealed record ContentUnit(string Channel, string Locale);

/// <summary>Content of one (channel, locale) pair of the pinned layout version.</summary>
public sealed record LayoutContentFacts(string Channel, string Locale, string Body, string? BodyText);

/// <summary>
/// Everything the layout-reference validation check needs to know about the
/// layout a template version pins. Built by the caller from the store, so the
/// validation catalog itself stays free of persistence concerns.
/// </summary>
public sealed record LayoutReferenceFacts
{
    public required string LayoutKey { get; init; }

    public required int LayoutVersion { get; init; }

    public required bool LayoutExists { get; init; }

    public required bool VersionExists { get; init; }

    /// <summary>Canonical status of the pinned version, when it exists.</summary>
    public string? VersionStatus { get; init; }

    /// <summary>Canonical status of the layout identity, when it exists.</summary>
    public string? LayoutStatus { get; init; }

    /// <summary>Default locale of the layout identity, when declared.</summary>
    public string? DefaultLocale { get; init; }

    /// <summary>Content of the pinned version, one entry per (channel, locale).</summary>
    public IReadOnlyList<LayoutContentFacts> Contents { get; init; } = [];

    /// <summary>
    /// Whether the pin resolves to layout text this version may adopt: the
    /// identity still takes a reference and the pinned version is published.
    /// Every rule that reads the layout text asks this first: a pin the
    /// layout-reference check already fails on stays out of the other rules,
    /// and their silence keeps the report pointing at the single real cause.
    /// </summary>
    public bool PinIsUsable => LayoutExists
        && IdentityTakesReference
        && VersionExists
        && string.Equals(VersionStatus, LayoutVersionStatuses.Published, StringComparison.Ordinal);

    /// <summary>
    /// Whether the layout identity still takes a reference. A status the
    /// caller did not project reads as one that does: a fact nobody supplied
    /// is not evidence of a refusal.
    /// </summary>
    private bool IdentityTakesReference =>
        !string.Equals(LayoutStatus, LayoutStatuses.Disabled, StringComparison.Ordinal)
        && !string.Equals(LayoutStatus, LayoutStatuses.Deprecated, StringComparison.Ordinal);

    /// <summary>
    /// The layout content that answers for a (channel, locale) pair, resolved
    /// through the chain the render itself walks: the exact locale, then its
    /// base language, then the default locale of the layout. Publication
    /// therefore rules on the very text that will wrap the message, not on the
    /// text of a neighbouring locale.
    /// </summary>
    public LayoutContentFacts? ResolveContent(Channel channel, Locale locale)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(locale);

        var candidates = Contents
            .Where(content => string.Equals(content.Channel, channel.Value, StringComparison.Ordinal))
            .ToList();
        Locale? resolved = LocaleResolution.Resolve(
            locale,
            candidates.Select(content => Locale.Trusted(content.Locale)).ToList(),
            DefaultLocale is null ? null : Locale.Trusted(DefaultLocale));
        return resolved is null
            ? null
            : candidates.FirstOrDefault(content =>
                string.Equals(content.Locale, resolved.Value, StringComparison.Ordinal));
    }
}
