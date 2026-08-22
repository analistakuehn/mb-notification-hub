using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>Lifecycle status of a layout identity.</summary>
public enum LayoutStatus
{
    Active = 1,
    Deprecated = 2,
    Disabled = 3,
}

/// <summary>Canonical wire and storage values for <see cref="LayoutStatus"/>.</summary>
public static class LayoutStatuses
{
    public const string Active = "active";
    public const string Deprecated = "deprecated";
    public const string Disabled = "disabled";

    public static IReadOnlyList<string> CanonicalValues { get; } = [Active, Deprecated, Disabled];

    public static string Canonical(this LayoutStatus value) => value switch
    {
        LayoutStatus.Active => Active,
        LayoutStatus.Deprecated => Deprecated,
        LayoutStatus.Disabled => Disabled,
        _ => throw new InvalidOperationException($"Unsupported layout status '{value}'."),
    };

    /// <summary>
    /// Lifecycle transition table for the layout identity. Deprecation and
    /// disablement are one-way: a disabled layout never comes back through
    /// the management API.
    /// </summary>
    public static IReadOnlyList<string> AllowedTransitions(LayoutStatus value) => value switch
    {
        LayoutStatus.Active => [Deprecated, Disabled],
        LayoutStatus.Deprecated => [Disabled],
        LayoutStatus.Disabled => [],
        _ => throw new InvalidOperationException($"Unsupported layout status '{value}'."),
    };

    public static Result<LayoutStatus> Create(string? value) => value?.Trim() switch
    {
        Active => Result.Success(LayoutStatus.Active),
        Deprecated => Result.Success(LayoutStatus.Deprecated),
        Disabled => Result.Success(LayoutStatus.Disabled),
        _ => Result.ValidationError<LayoutStatus>(DomainError.Format(
            ErrorCodes.InvalidRequest,
            $"Unknown status '{value}'. Supported statuses: {string.Join(", ", CanonicalValues)}.")),
    };

    /// <summary>Rehydrates a status that already passed validation (persistence, canonical data).</summary>
    public static LayoutStatus Trusted(string value)
    {
        Result<LayoutStatus> result = Create(value);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException($"Unknown persisted layout status '{value}'.");
    }
}
