using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

public sealed class AttachmentManagementEfOptions
{
    public const string SectionName = "Modules:AttachmentManagement:Persistence:Ef";

    [Required]
    public required string ConnectionString { get; init; }

    public bool EnableSensitiveDataLogging { get; init; }

    public bool EnableDetailedErrors { get; init; }
}
