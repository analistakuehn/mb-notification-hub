using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

public sealed class AuditEfOptions
{
    public const string SectionName = "Modules:Audit:Persistence:Ef";

    /// <summary>
    /// Must point at the same physical database as every module whose effects
    /// this trail records: the transactional append shares the caller's
    /// database transaction, which only exists inside one database.
    /// </summary>
    [Required]
    public required string ConnectionString { get; init; }

    public bool EnableSensitiveDataLogging { get; init; }

    public bool EnableDetailedErrors { get; init; }
}
