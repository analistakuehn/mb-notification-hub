namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>Lifecycle status of a class policy version.</summary>
public enum ClassPolicyVersionStatus
{
    Draft = 1,
    Published = 2,
    Superseded = 3,
}

/// <summary>
/// Canonical wire and storage values plus the transition table for
/// <see cref="ClassPolicyVersionStatus"/>. A published version is immutable; the
/// only way forward is being superseded by the next published version.
/// </summary>
public static class ClassPolicyVersionStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Superseded = "superseded";

    public static string Canonical(this ClassPolicyVersionStatus value) => value switch
    {
        ClassPolicyVersionStatus.Draft => Draft,
        ClassPolicyVersionStatus.Published => Published,
        ClassPolicyVersionStatus.Superseded => Superseded,
        _ => throw new InvalidOperationException($"Unsupported class policy version status '{value}'."),
    };

    public static IReadOnlyList<string> AllowedTransitions(ClassPolicyVersionStatus value) => value switch
    {
        ClassPolicyVersionStatus.Draft => [Published],
        ClassPolicyVersionStatus.Published => [Superseded],
        ClassPolicyVersionStatus.Superseded => [],
        _ => throw new InvalidOperationException($"Unsupported class policy version status '{value}'."),
    };

    /// <summary>Rehydrates a status that already passed validation (persistence, canonical data).</summary>
    public static ClassPolicyVersionStatus Trusted(string value) => value switch
    {
        Draft => ClassPolicyVersionStatus.Draft,
        Published => ClassPolicyVersionStatus.Published,
        Superseded => ClassPolicyVersionStatus.Superseded,
        _ => throw new InvalidOperationException($"Unknown persisted class policy version status '{value}'."),
    };
}
