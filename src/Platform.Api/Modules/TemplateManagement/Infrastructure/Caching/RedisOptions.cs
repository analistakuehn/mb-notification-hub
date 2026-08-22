using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Caching;

public sealed class RedisOptions
{
    public const string SectionName = "Modules:TemplateManagement:Cache:Redis";

    [Required]
    public required string ConnectionString { get; init; }

    public string InstanceName { get; init; } = string.Empty;
}
