using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.IntegrationTests.Dispatch;

internal sealed class FakeChannelProvider(Channel channel, string providerKey) : IChannelProvider
{
    public Channel Channel => channel;

    public string ProviderKey => providerKey;

    public Task<ProviderResult> SendAsync(DispatchRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ProviderResult.Accepted("fake-id"));
}
