namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>One (channel, locale) coordinate of a content entry.</summary>
public sealed record ContentUnit(string Channel, string Locale);

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

    /// <summary>Default locale of the layout identity, when declared.</summary>
    public string? DefaultLocale { get; init; }

    /// <summary>(channel, locale) coordinates the pinned version has content for.</summary>
    public IReadOnlyList<ContentUnit> Contents { get; init; } = [];
}
