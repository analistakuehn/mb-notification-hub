using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

public sealed class NotificationsEfOptions
{
    public const string SectionName = "Modules:Notifications:Persistence:Ef";

    /// <summary>
    /// Must point at the same physical database as the Audit module and the
    /// platform outbox: the ingestion commits notification, idempotency key,
    /// outbox message and audit event in one database transaction, which only
    /// exists inside one database.
    /// </summary>
    [Required]
    public required string ConnectionString { get; init; }

    public bool EnableSensitiveDataLogging { get; init; }

    public bool EnableDetailedErrors { get; init; }
}
