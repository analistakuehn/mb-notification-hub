using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// One grant of the producer registry: a bus principal may request one class
/// for one application. The canonical form of this data lives in the
/// infrastructure repository; a deploy job materializes it into the table and
/// the hub only ever reads it at runtime, so every grant carries the review
/// trail of the change that created it.
///
/// There is no enabled flag on purpose: a switched-off row would be a slow
/// lever pretending to be an emergency stop. Cutting a producer off is the
/// kill switch and the broker ACL; the registry answers what is granted.
/// </summary>
public sealed class ProducerRegistration
{
    private ProducerRegistration(
        string principal,
        string application,
        string notificationClass,
        DateTimeOffset updatedAt)
    {
        Principal = principal;
        Application = application;
        Class = notificationClass;
        UpdatedAt = updatedAt;
    }

    // EF Core materialization: fields are populated from the store.
    private ProducerRegistration()
    {
        Principal = null!;
        Application = null!;
        Class = null!;
    }

    /// <summary>Bus principal of the producer, as the broker authenticates it.</summary>
    public string Principal { get; }

    /// <summary>Application the grant covers.</summary>
    public string Application { get; }

    /// <summary>Canonical notification class the grant covers.</summary>
    public string Class { get; }

    public DateTimeOffset UpdatedAt { get; }

    public static Result<ProducerRegistration> Create(
        string? principal,
        string? application,
        string? notificationClass,
        DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(principal))
        {
            return Result.ValidationError<ProducerRegistration>("Producer principal must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(application))
        {
            return Result.ValidationError<ProducerRegistration>("Application must not be empty.");
        }

        if (!NotificationClasses.IsCanonical(notificationClass))
        {
            return Result.ValidationError<ProducerRegistration>(
                $"Class must be one of: {string.Join(", ", NotificationClasses.CanonicalValues)}.");
        }

        return Result.Success(new ProducerRegistration(
            principal.Trim(), application.Trim(), notificationClass!, updatedAt));
    }
}
