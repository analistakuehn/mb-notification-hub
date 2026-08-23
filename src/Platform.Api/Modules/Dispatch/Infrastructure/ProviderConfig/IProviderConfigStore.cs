using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.ProviderConfig;

/// <summary>Read side of the materialized provider configuration.</summary>
internal interface IProviderConfigStore
{
    /// <summary>
    /// Provider key that currently delivers <paramref name="channel"/>: the
    /// lowest-priority configured row for that channel.
    /// </summary>
    Task<Result<string>> ResolveProviderKeyAsync(Channel channel, CancellationToken cancellationToken);
}
