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

    /// <summary>
    /// Optional connection of the query surface. Absent means the query reads
    /// the same database as the write path, which is the correct default until
    /// a physical read replica exists: the seam is here so pointing the query
    /// at the replica later is configuration, not code.
    /// </summary>
    public string? ReadConnectionString { get; init; }

    /// <summary>The connection the read-only context opens; the write connection when no replica is configured.</summary>
    public string EffectiveReadConnectionString
        => string.IsNullOrWhiteSpace(ReadConnectionString) ? ConnectionString : ReadConnectionString;

    public bool EnableSensitiveDataLogging { get; init; }

    public bool EnableDetailedErrors { get; init; }
}
