namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>Lifecycle status of a template version.</summary>
public enum TemplateVersionStatus
{
    Draft = 1,
    Published = 2,
    Superseded = 3,
}

/// <summary>
/// Canonical wire and storage values plus the transition table for
/// <see cref="TemplateVersionStatus"/>. A published version is immutable; the only
/// way forward is being superseded by the next published version.
/// </summary>
public static class TemplateVersionStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Superseded = "superseded";

    public static string Canonical(this TemplateVersionStatus value) => value switch
    {
        TemplateVersionStatus.Draft => Draft,
        TemplateVersionStatus.Published => Published,
        TemplateVersionStatus.Superseded => Superseded,
        _ => throw new InvalidOperationException($"Unsupported template version status '{value}'."),
    };

    public static IReadOnlyList<string> AllowedTransitions(TemplateVersionStatus value) => value switch
    {
        TemplateVersionStatus.Draft => [Published],
        TemplateVersionStatus.Published => [Superseded],
        TemplateVersionStatus.Superseded => [],
        _ => throw new InvalidOperationException($"Unsupported template version status '{value}'."),
    };

    /// <summary>Rehydrates a status that already passed validation (persistence, canonical data).</summary>
    public static TemplateVersionStatus Trusted(string value) => value switch
    {
        Draft => TemplateVersionStatus.Draft,
        Published => TemplateVersionStatus.Published,
        Superseded => TemplateVersionStatus.Superseded,
        _ => throw new InvalidOperationException($"Unknown persisted template version status '{value}'."),
    };
}
