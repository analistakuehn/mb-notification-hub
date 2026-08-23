using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;

public sealed class DispatchEfOptions
{
    public const string SectionName = "Modules:Dispatch:Persistence:Ef";

    [Required]
    public required string ConnectionString { get; init; }

    public bool EnableSensitiveDataLogging { get; init; }

    public bool EnableDetailedErrors { get; init; }
}
