using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Dispatch.Domain;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.ProviderConfig;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.Dispatch;

[Collection(DispatchPostgresCollectionDefinition.Name)]
public sealed class ProviderConfigResolutionTests(DispatchPostgresFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [RequiresDockerFact]
    public async Task Resolves_the_lowest_priority_provider_configured_for_each_channel()
    {
        await ResetTableAsync();
        await SeedAsync(("email", "sendgrid", 0), ("email", "acme-mail", 1), ("push", "fcm", 0));
        var clock = new MutableTimeProvider(Start);
        await using ServiceProvider services = DispatchTestServices.BuildResolutionHost(
            fixture.ConnectionString,
            clock,
            new FakeChannelProvider(Channel.Email, "sendgrid"),
            new FakeChannelProvider(Channel.Push, "fcm"));
        IChannelProviderResolver resolver = services.GetRequiredService<IChannelProviderResolver>();

        Result<IChannelProvider> email = await resolver.ResolveAsync(Channel.Email, CancellationToken.None);
        Result<IChannelProvider> push = await resolver.ResolveAsync(Channel.Push, CancellationToken.None);

        email.IsSuccess.ShouldBeTrue();
        email.Value!.ProviderKey.ShouldBe("sendgrid");
        push.IsSuccess.ShouldBeTrue();
        push.Value!.ProviderKey.ShouldBe("fcm");
    }

    [RequiresDockerFact]
    public async Task Fails_as_integration_error_when_the_channel_has_no_configured_provider()
    {
        await ResetTableAsync();
        await SeedAsync(("email", "sendgrid", 0));
        var clock = new MutableTimeProvider(Start);
        await using ServiceProvider services = DispatchTestServices.BuildResolutionHost(
            fixture.ConnectionString, clock, new FakeChannelProvider(Channel.Email, "sendgrid"));
        IChannelProviderResolver resolver = services.GetRequiredService<IChannelProviderResolver>();

        Result<IChannelProvider> result = await resolver.ResolveAsync(Channel.Sms, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Integration);
        result.Error!.ShouldContain("sms");
    }

    [RequiresDockerFact]
    public async Task A_configuration_change_lands_only_after_the_cache_ttl_expires()
    {
        await ResetTableAsync();
        await SeedAsync(("email", "sendgrid", 0));
        var clock = new MutableTimeProvider(Start);
        await using ServiceProvider services = DispatchTestServices.BuildResolutionHost(
            fixture.ConnectionString, clock, new FakeChannelProvider(Channel.Email, "sendgrid"));
        IProviderConfigStore store = services.GetRequiredService<IProviderConfigStore>();

        Result<string> first = await store.ResolveProviderKeyAsync(Channel.Email, CancellationToken.None);
        first.Value.ShouldBe("sendgrid");

        await ResetTableAsync();
        await SeedAsync(("email", "acme-mail", 0));

        clock.Advance(TimeSpan.FromSeconds(59));
        Result<string> withinTtl = await store.ResolveProviderKeyAsync(Channel.Email, CancellationToken.None);
        withinTtl.Value.ShouldBe("sendgrid");

        clock.Advance(TimeSpan.FromSeconds(2));
        Result<string> afterTtl = await store.ResolveProviderKeyAsync(Channel.Email, CancellationToken.None);
        afterTtl.Value.ShouldBe("acme-mail");
    }

    [RequiresDockerFact]
    public async Task A_channel_added_to_the_table_becomes_resolvable_after_expiry_without_restart()
    {
        await ResetTableAsync();
        await SeedAsync(("email", "sendgrid", 0));
        var clock = new MutableTimeProvider(Start);
        await using ServiceProvider services = DispatchTestServices.BuildResolutionHost(
            fixture.ConnectionString, clock, new FakeChannelProvider(Channel.Email, "sendgrid"));
        IProviderConfigStore store = services.GetRequiredService<IProviderConfigStore>();

        (await store.ResolveProviderKeyAsync(Channel.Push, CancellationToken.None))
            .IsFailure.ShouldBeTrue();

        await SeedAsync(("push", "fcm", 0));
        clock.Advance(TimeSpan.FromSeconds(61));

        Result<string> resolved = await store.ResolveProviderKeyAsync(Channel.Push, CancellationToken.None);
        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value.ShouldBe("fcm");
    }

    private async Task ResetTableAsync()
    {
        await using DispatchDbContext context = fixture.CreateDbContext();
        await context.ProviderSelections.ExecuteDeleteAsync();
    }

    private async Task SeedAsync(params (string Channel, string ProviderKey, int Priority)[] rows)
    {
        await using DispatchDbContext context = fixture.CreateDbContext();
        foreach ((var channel, var providerKey, var priority) in rows)
        {
            Result<ProviderSelection> selection = ProviderSelection.Create(
                channel, providerKey, priority, Start);
            selection.IsSuccess.ShouldBeTrue(selection.Error);
            context.ProviderSelections.Add(selection.Value!);
        }

        await context.SaveChangesAsync();
    }
}
