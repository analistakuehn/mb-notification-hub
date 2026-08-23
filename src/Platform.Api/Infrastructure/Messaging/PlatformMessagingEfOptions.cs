using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Infrastructure.Messaging;

public sealed class PlatformMessagingEfOptions
{
    public const string SectionName = "Platform:Messaging:Ef";

    /// <summary>
    /// Must point at the same physical database as every module that appends
    /// to the outbox: the transactional append shares the caller's database
    /// transaction, which only exists inside one database.
    /// </summary>
    [Required]
    public required string ConnectionString { get; init; }

    public bool EnableSensitiveDataLogging { get; init; }

    public bool EnableDetailedErrors { get; init; }
}
