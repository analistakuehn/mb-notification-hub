using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.ProviderConfig;

/// <summary>
/// Joins the configured provider key of a channel with the adapter instance
/// this process hosts. A configured key without a hosted adapter, or an
/// adapter registered for a different channel, is a deployment defect and
/// surfaces as an integration failure with the exact mismatch.
/// </summary>
internal sealed class ChannelProviderResolver(
    IProviderConfigStore configStore,
    IEnumerable<IChannelProvider> providers) : IChannelProviderResolver
{
    public async Task<Result<IChannelProvider>> ResolveAsync(
        Channel channel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        Result<string> providerKey = await configStore.ResolveProviderKeyAsync(channel, cancellationToken);
        if (providerKey.IsFailure)
        {
            return new Result<IChannelProvider>(false, default, providerKey.ErrorKind, providerKey.Error);
        }

        IChannelProvider? provider = providers.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderKey, providerKey.Value, StringComparison.Ordinal));
        if (provider is null)
        {
            return Result.IntegrationFailure<IChannelProvider>(
                $"Channel '{channel.Value}' is configured for provider '{providerKey.Value}', "
                + "but no adapter with that key is hosted in this process.");
        }

        if (!ReferenceEquals(provider.Channel, channel))
        {
            return Result.IntegrationFailure<IChannelProvider>(
                $"Provider '{provider.ProviderKey}' delivers channel '{provider.Channel.Value}', "
                + $"but the configuration selects it for channel '{channel.Value}'.");
        }

        return Result.Success(provider);
    }
}
