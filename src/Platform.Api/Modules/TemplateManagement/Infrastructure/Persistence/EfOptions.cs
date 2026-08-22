using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;

public sealed class EfOptions
{
    public const string SectionName = "Modules:TemplateManagement:Persistence:Ef";

    [Required]
    public required string ConnectionString { get; init; }

    public bool EnableSensitiveDataLogging { get; init; }

    public bool EnableDetailedErrors { get; init; }
}
