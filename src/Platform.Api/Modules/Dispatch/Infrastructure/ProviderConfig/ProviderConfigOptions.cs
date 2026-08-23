using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.ProviderConfig;

public sealed class ProviderConfigOptions
{
    public const string SectionName = "Modules:Dispatch:ProviderConfig";

    /// <summary>
    /// How long one loaded snapshot of the provider configuration table
    /// serves resolutions before the next read refreshes it.
    /// </summary>
    [Range(1, 3600)]
    public int CacheTtlSeconds { get; init; } = 60;
}
