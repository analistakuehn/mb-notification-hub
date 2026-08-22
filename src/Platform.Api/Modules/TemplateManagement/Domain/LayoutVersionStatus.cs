namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>Lifecycle status of a layout version.</summary>
public enum LayoutVersionStatus
{
    Draft = 1,
    Published = 2,
    Superseded = 3,
}

/// <summary>
/// Canonical wire and storage values plus the transition table for
/// <see cref="LayoutVersionStatus"/>. A published version is immutable; the only
/// way forward is being superseded by the next published version.
/// </summary>
public static class LayoutVersionStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Superseded = "superseded";

    public static string Canonical(this LayoutVersionStatus value) => value switch
    {
        LayoutVersionStatus.Draft => Draft,
        LayoutVersionStatus.Published => Published,
        LayoutVersionStatus.Superseded => Superseded,
        _ => throw new InvalidOperationException($"Unsupported layout version status '{value}'."),
    };

    public static IReadOnlyList<string> AllowedTransitions(LayoutVersionStatus value) => value switch
    {
        LayoutVersionStatus.Draft => [Published],
        LayoutVersionStatus.Published => [Superseded],
        LayoutVersionStatus.Superseded => [],
        _ => throw new InvalidOperationException($"Unsupported layout version status '{value}'."),
    };

    /// <summary>Rehydrates a status that already passed validation (persistence, canonical data).</summary>
    public static LayoutVersionStatus Trusted(string value) => value switch
    {
        Draft => LayoutVersionStatus.Draft,
        Published => LayoutVersionStatus.Published,
        Superseded => LayoutVersionStatus.Superseded,
        _ => throw new InvalidOperationException($"Unknown persisted layout version status '{value}'."),
    };
}
