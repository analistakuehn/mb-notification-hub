using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;

public sealed class ContactConsentEfOptions
{
    public const string SectionName = "Modules:ContactConsent:Persistence:Ef";

    /// <summary>
    /// Must point at the same physical database as the Audit module and the
    /// platform outbox: every write commits its rows, its outbox messages and
    /// its audit event in one database transaction, which only exists inside
    /// one database.
    /// </summary>
    [Required]
    public required string ConnectionString { get; init; }

    public bool EnableSensitiveDataLogging { get; init; }

    public bool EnableDetailedErrors { get; init; }
}
