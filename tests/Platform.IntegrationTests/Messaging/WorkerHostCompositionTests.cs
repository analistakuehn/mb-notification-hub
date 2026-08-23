using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
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
    public void The_core_role_hosts_exactly_the_core_consumer_and_the_processed_purge()
    {
        HostApplicationBuilder builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Worker:Role"] = WorkerRoleCatalog.CoreRole,
            ["Platform:Messaging:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
            ["Platform:Cryptography:Envelope:KeyId"] = "test-key",
            ["Platform:Cryptography:Envelope:MasterKey"] = Convert.ToBase64String(new byte[32]),
            ["Modules:Notifications:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
            ["Modules:Notifications:Redis:ConnectionString"] = "localhost:6379",
            ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
            ["Modules:ContactConsent:Redis:ConnectionString"] = "localhost:6379",
            ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
        });

        WorkerRoleCatalog.Register(builder.Services, builder.Configuration);
        using IHost host = builder.Build();

        IHostedService[] hosted = [.. host.Services.GetServices<IHostedService>()];
        hosted.OfType<SqsConsumerService<CoreMessageProcessor>>().ShouldHaveSingleItem();
        hosted.OfType<ProcessedMessagePurgeService>().ShouldHaveSingleItem();
        hosted
            .Where(service => service is not SqsConsumerService<CoreMessageProcessor>
                && service is not ProcessedMessagePurgeService)
            .ShouldAllBe(service => service.GetType().Namespace!.StartsWith("Microsoft.", StringComparison.Ordinal));

        // The role consumes the sibling contexts through their published
        // read contracts and the pipeline resolves end to end.
        using IServiceScope scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRecipientDirectory>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<NotificationPipeline>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<CoreMessageProcessor>().ShouldNotBeNull();
        host.Services.GetRequiredService<SqsConsumerPlan<CoreMessageProcessor>>()
            .Queues.Select(binding => binding.QueueName)
            .ShouldBe(["core-auth", "core-critical", "core-transactional", "core-operational"]);
    }

    [Fact]
    public void The_contact_consent_role_hosts_exactly_the_invalidation_consumer_and_the_processed_purge()
    {
        HostApplicationBuilder builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Worker:Role"] = WorkerRoleCatalog.ContactConsentRole,
            ["Platform:Messaging:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
            ["Platform:Cryptography:Envelope:KeyId"] = "test-key",
            ["Platform:Cryptography:Envelope:MasterKey"] = Convert.ToBase64String(new byte[32]),
            ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
            ["Modules:ContactConsent:Redis:ConnectionString"] = "localhost:6379",
        });

        WorkerRoleCatalog.Register(builder.Services, builder.Configuration);
        using IHost host = builder.Build();

        IHostedService[] hosted = [.. host.Services.GetServices<IHostedService>()];
        hosted.OfType<SqsConsumerService<ContactsChangedProcessor>>().ShouldHaveSingleItem();
        hosted.OfType<ProcessedMessagePurgeService>().ShouldHaveSingleItem();
        hosted
            .Where(service => service is not SqsConsumerService<ContactsChangedProcessor>
                && service is not ProcessedMessagePurgeService)
            .ShouldAllBe(service => service.GetType().Namespace!.StartsWith("Microsoft.", StringComparison.Ordinal));

        host.Services.GetRequiredService<SqsConsumerPlan<ContactsChangedProcessor>>()
            .Queues.ShouldHaveSingleItem().QueueName.ShouldBe("contacts-changed");
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
