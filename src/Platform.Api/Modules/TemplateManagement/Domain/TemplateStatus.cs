using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>Lifecycle status of a template identity.</summary>
public enum TemplateStatus
{
    Active = 1,
    Deprecated = 2,
    Disabled = 3,
}

/// <summary>Canonical wire and storage values for <see cref="TemplateStatus"/>.</summary>
public static class TemplateStatuses
{
    public const string Active = "active";
    public const string Deprecated = "deprecated";
    public const string Disabled = "disabled";

    public static IReadOnlyList<string> CanonicalValues { get; } = [Active, Deprecated, Disabled];

    public static string Canonical(this TemplateStatus value) => value switch
    {
        TemplateStatus.Active => Active,
        TemplateStatus.Deprecated => Deprecated,
        TemplateStatus.Disabled => Disabled,
        _ => throw new InvalidOperationException($"Unsupported template status '{value}'."),
    };

    /// <summary>
    /// Lifecycle transition table for the template identity. Deprecation and
    /// disablement are one-way: a disabled template never comes back through
    /// the management API.
    /// </summary>
    public static IReadOnlyList<string> AllowedTransitions(TemplateStatus value) => value switch
    {
        TemplateStatus.Active => [Deprecated, Disabled],
        TemplateStatus.Deprecated => [Disabled],
        TemplateStatus.Disabled => [],
        _ => throw new InvalidOperationException($"Unsupported template status '{value}'."),
    };

    public static Result<TemplateStatus> Create(string? value) => value?.Trim() switch
    {
        Active => Result.Success(TemplateStatus.Active),
        Deprecated => Result.Success(TemplateStatus.Deprecated),
        Disabled => Result.Success(TemplateStatus.Disabled),
        _ => Result.ValidationError<TemplateStatus>(DomainError.Format(
            ErrorCodes.InvalidRequest,
            $"Unknown status '{value}'. Supported statuses: {string.Join(", ", CanonicalValues)}.")),
    };

    /// <summary>Rehydrates a status that already passed validation (persistence, canonical data).</summary>
    public static TemplateStatus Trusted(string value)
    {
        Result<TemplateStatus> result = Create(value);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException($"Unknown persisted template status '{value}'.");
    }
}
