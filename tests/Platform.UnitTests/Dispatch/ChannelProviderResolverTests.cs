using NSubstitute;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.ProviderConfig;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.Dispatch;

public sealed class ChannelProviderResolverTests
{
    [Fact]
    public async Task Resolves_the_adapter_named_by_the_configuration()
    {
        IProviderConfigStore store = Substitute.For<IProviderConfigStore>();
        store.ResolveProviderKeyAsync(Channel.Email, Arg.Any<CancellationToken>())
            .Returns(Result.Success("sendgrid"));
        var resolver = new ChannelProviderResolver(
            store, [new FakeProvider(Channel.Email, "sendgrid"), new FakeProvider(Channel.Push, "fcm")]);

        Result<IChannelProvider> result = await resolver.ResolveAsync(Channel.Email, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.ProviderKey.ShouldBe("sendgrid");
    }

    [Fact]
    public async Task Fails_when_the_configured_key_has_no_hosted_adapter()
    {
        IProviderConfigStore store = Substitute.For<IProviderConfigStore>();
        store.ResolveProviderKeyAsync(Channel.Email, Arg.Any<CancellationToken>())
            .Returns(Result.Success("acme-mail"));
        var resolver = new ChannelProviderResolver(store, [new FakeProvider(Channel.Email, "sendgrid")]);

        Result<IChannelProvider> result = await resolver.ResolveAsync(Channel.Email, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Integration);
        result.Error!.ShouldContain("acme-mail");
    }

    [Fact]
    public async Task Fails_when_the_configured_adapter_delivers_another_channel()
    {
        IProviderConfigStore store = Substitute.For<IProviderConfigStore>();
        store.ResolveProviderKeyAsync(Channel.Email, Arg.Any<CancellationToken>())
            .Returns(Result.Success("fcm"));
        var resolver = new ChannelProviderResolver(store, [new FakeProvider(Channel.Push, "fcm")]);

        Result<IChannelProvider> result = await resolver.ResolveAsync(Channel.Email, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Integration);
        result.Error!.ShouldContain("push");
    }

    [Fact]
    public async Task Propagates_a_configuration_read_failure()
    {
        IProviderConfigStore store = Substitute.For<IProviderConfigStore>();
        store.ResolveProviderKeyAsync(Channel.Email, Arg.Any<CancellationToken>())
            .Returns(Result.IntegrationFailure<string>("No provider is configured for channel 'email'."));
        var resolver = new ChannelProviderResolver(store, []);

        Result<IChannelProvider> result = await resolver.ResolveAsync(Channel.Email, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Integration);
        result.Error!.ShouldContain("email");
    }

    private sealed class FakeProvider(Channel channel, string providerKey) : IChannelProvider
    {
        public Channel Channel => channel;

        public string ProviderKey => providerKey;

        public Task<ProviderResult> SendAsync(DispatchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(ProviderResult.Accepted("fake-id"));
    }
}
