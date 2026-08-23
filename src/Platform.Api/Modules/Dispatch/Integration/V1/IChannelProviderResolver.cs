using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Resolves the provider adapter that delivers a channel, following the
/// materialized provider configuration table (cached briefly, so a
/// configuration change lands without a deploy). Resolution failure is an
/// integration failure: no configured row, or a row naming an adapter this
/// process does not host.
/// </summary>
public interface IChannelProviderResolver
{
    Task<Result<IChannelProvider>> ResolveAsync(Channel channel, CancellationToken cancellationToken);
}
