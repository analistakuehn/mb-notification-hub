using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>Regulatory class of the communication a template produces.</summary>
public enum NotificationClass
{
    Critical = 1,
    Transactional = 2,
    Operational = 3,
}

/// <summary>Canonical wire and storage values for <see cref="NotificationClass"/>.</summary>
public static class NotificationClasses
{
    public const string Critical = "critical";
    public const string Transactional = "transactional";
    public const string Operational = "operational";

    public static IReadOnlyList<string> CanonicalValues { get; } = [Critical, Transactional, Operational];

    public static string Canonical(this NotificationClass value) => value switch
    {
        NotificationClass.Critical => Critical,
        NotificationClass.Transactional => Transactional,
        NotificationClass.Operational => Operational,
        _ => throw new InvalidOperationException($"Unsupported notification class '{value}'."),
    };

    public static Result<NotificationClass> Create(string? value) => value?.Trim() switch
    {
        Critical => Result.Success(NotificationClass.Critical),
        Transactional => Result.Success(NotificationClass.Transactional),
        Operational => Result.Success(NotificationClass.Operational),
        _ => Result.ValidationError<NotificationClass>(DomainError.Format(
            ErrorCodes.InvalidRequest,
            $"Unknown class '{value}'. Supported classes: {string.Join(", ", CanonicalValues)}.")),
    };

    /// <summary>Rehydrates a class that already passed validation (persistence, canonical data).</summary>
    public static NotificationClass Trusted(string value)
    {
        Result<NotificationClass> result = Create(value);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException($"Unknown persisted notification class '{value}'.");
    }
}
