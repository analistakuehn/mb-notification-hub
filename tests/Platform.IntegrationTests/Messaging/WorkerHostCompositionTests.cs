using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.Worker;

namespace NotificationHub.IntegrationTests.Messaging;

/// <summary>
/// The worker host is a thin composition root: these tests exercise the role
/// catalog its <c>Program</c> delegates to, against a real host builder.
/// </summary>
public sealed class WorkerHostCompositionTests
{
    [Fact]
    public void The_outbox_relay_role_hosts_exactly_the_relay_service()
    {
        HostApplicationBuilder builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Worker:Role"] = WorkerRoleCatalog.OutboxRelayRole,
            ["Platform:Messaging:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
        });

        WorkerRoleCatalog.Register(builder.Services, builder.Configuration);
        using IHost host = builder.Build();

        // The role hosts exactly one service of its own; anything else in the
        // hosted set is framework plumbing (the health-check publisher).
        IHostedService[] hosted = [.. host.Services.GetServices<IHostedService>()];
        hosted.OfType<OutboxRelayService>().ShouldHaveSingleItem();
        hosted
            .Where(service => service is not OutboxRelayService)
            .ShouldAllBe(service => service.GetType().Namespace!.StartsWith("Microsoft.", StringComparison.Ordinal));
        // The platform messaging composition came along: the relay reads the
        // same outbox the producing modules append to.
        host.Services.GetRequiredService<IOutboxWriter>().ShouldNotBeNull();
    }

    [Fact]
    public void Boot_fails_with_a_clear_message_when_no_role_is_configured()
    {
        HostApplicationBuilder builder = CreateBuilder([]);

        InvalidOperationException failure = Should.Throw<InvalidOperationException>(
            () => WorkerRoleCatalog.Register(builder.Services, builder.Configuration));

        failure.Message.ShouldContain(WorkerRoleCatalog.RoleConfigurationKey);
        failure.Message.ShouldContain(WorkerRoleCatalog.OutboxRelayRole);
    }

    [Fact]
    public void Boot_fails_with_a_clear_message_for_an_unknown_role()
    {
        HostApplicationBuilder builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Worker:Role"] = "core-pipeline",
        });

        InvalidOperationException failure = Should.Throw<InvalidOperationException>(
            () => WorkerRoleCatalog.Register(builder.Services, builder.Configuration));

        failure.Message.ShouldContain("core-pipeline");
        failure.Message.ShouldContain(WorkerRoleCatalog.OutboxRelayRole);
    }

    private static HostApplicationBuilder CreateBuilder(Dictionary<string, string?> settings)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        return builder;
    }
}
