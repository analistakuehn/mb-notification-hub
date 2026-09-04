using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reconciliation;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
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
    public void The_dispatcher_role_hosts_exactly_the_dispatch_consumer_and_the_processed_purge()
    {
        HostApplicationBuilder builder = CreateBuilder(DispatcherSettings());

        WorkerRoleCatalog.Register(builder.Services, builder.Configuration);
        using IHost host = builder.Build();

        IHostedService[] hosted = [.. host.Services.GetServices<IHostedService>()];
        hosted.OfType<SqsConsumerService<DispatchMessageProcessor>>().ShouldHaveSingleItem();
        hosted.OfType<ProcessedMessagePurgeService>().ShouldHaveSingleItem();
        hosted
            .Where(service => service is not SqsConsumerService<DispatchMessageProcessor>
                && service is not ProcessedMessagePurgeService)
            .ShouldAllBe(service => service.GetType().Namespace!.StartsWith("Microsoft.", StringComparison.Ordinal));

        // The role consumes the sibling contexts through their published
        // contracts and drains the product of hosted channels and bands.
        using IServiceScope scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRecipientDirectory>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IDeviceTokenLifecycle>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IChannelProviderResolver>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<DispatchMessageProcessor>().ShouldNotBeNull();
        host.Services.GetRequiredService<SqsConsumerPlan<DispatchMessageProcessor>>()
            .Queues.Select(binding => binding.QueueName)
            .ShouldBe(
            [
                "dispatch-email-auth", "dispatch-sms-auth", "dispatch-push-auth",
                "dispatch-email-critical", "dispatch-sms-critical", "dispatch-push-critical",
                "dispatch-email-transactional", "dispatch-sms-transactional", "dispatch-push-transactional",
                "dispatch-email-operational", "dispatch-sms-operational", "dispatch-push-operational",
            ]);
    }

    [Fact]
    public void The_dispatcher_role_refuses_to_boot_for_a_known_channel_without_a_hosted_adapter()
    {
        Dictionary<string, string?> settings = DispatcherSettings();
        settings["Modules:Notifications:Dispatcher:Channels:0"] = "whatsapp";
        HostApplicationBuilder builder = CreateBuilder(settings);

        Should.Throw<InvalidOperationException>(
                () => WorkerRoleCatalog.Register(builder.Services, builder.Configuration))
            .Message.ShouldContain("whatsapp");
    }

    private static Dictionary<string, string?> DispatcherSettings()
        => new()
        {
            ["Worker:Role"] = "dispatcher",
            ["Platform:Messaging:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
            ["Platform:Cryptography:Envelope:KeyId"] = "test-key",
            ["Platform:Cryptography:Envelope:MasterKey"] = Convert.ToBase64String(new byte[32]),
            ["Modules:Notifications:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
            ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
            ["Modules:ContactConsent:Redis:ConnectionString"] = "localhost:6379",
            ["Modules:Dispatch:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
            ["Modules:AttachmentManagement:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
            ["Modules:AttachmentManagement:Capacity:MaxAttachmentBytes"] = "7340032",
            ["Modules:AttachmentManagement:Capacity:MaxEnvelopeBytes"] = "7340032",
            ["Modules:AttachmentManagement:Capacity:MaxAttachmentsPerNotification"] = "10",
        };

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
    public void The_notifications_maintenance_role_hosts_the_content_services_the_hold_releaser_and_the_reconciliation()
    {
        HostApplicationBuilder builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Worker:Role"] = "notifications-maintenance",
            ["Platform:Cryptography:Envelope:KeyId"] = "test-key",
            ["Platform:Cryptography:Envelope:MasterKey"] = Convert.ToBase64String(new byte[32]),
            ["Modules:Notifications:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
            ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",

            // The reconciliation reads contacts to reveal a destination and
            // reports a refused one back, so the role composes that context's
            // stores exactly like the roles that already write to it.
            ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=worker_tests;Username=test",
            ["Modules:ContactConsent:Redis:ConnectionString"] = "localhost:6379",
        });

        WorkerRoleCatalog.Register(builder.Services, builder.Configuration);
        using IHost host = builder.Build();

        IHostedService[] hosted = [.. host.Services.GetServices<IHostedService>()];
        hosted.OfType<RenderedContentSweepService>().ShouldHaveSingleItem();
        hosted.OfType<RenderedContentBackfillService>().ShouldHaveSingleItem();
        hosted.OfType<KillSwitchHoldReleaseService>().ShouldHaveSingleItem();
        hosted.OfType<DeliveryReconciliationService>().ShouldHaveSingleItem();
        hosted
            .Where(service => service is not RenderedContentSweepService
                && service is not RenderedContentBackfillService
                && service is not KillSwitchHoldReleaseService
                && service is not DeliveryReconciliationService)
            .ShouldAllBe(service => service.GetType().Namespace!.StartsWith("Microsoft.", StringComparison.Ordinal));

        // The backfill rebuilds the masked form through the published render
        // contract, so the role composes it exactly like the pipeline does.
        using IServiceScope scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IPublishedTemplateRenderer>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<RenderedContentSweep>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<RenderedContentBackfill>().ShouldNotBeNull();

        // The whole reconciliation resolves, which is the only proof that the
        // role composed the three sibling surfaces it needs: the providers it
        // may ask, the contacts it may reveal a destination from, and the
        // trail every correction writes.
        scope.ServiceProvider.GetRequiredService<DeliveryReconciliationScan>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IProviderDeliveryLookupResolver>()
            .Resolve("fcm").IsFailure.ShouldBeTrue(
                "o provedor de push não registra consulta posterior, e a recusa é o registro disso.");
    }

    /// <summary>
    /// The attachment maintenance role composes the repair round and nothing
    /// that answers a caller.
    /// <para>
    /// The whole round resolving is the proof the role composed the surfaces
    /// it needs: the module's own store, the custody it removes through, the
    /// inventory it discovers unaccounted generations through, and the
    /// validation that owns the state machine a waiting verdict ends in. A
    /// role that composed three of the four would fail on the first round in
    /// production and never here.
    /// </para>
    /// </summary>
    [Fact]
    public void The_attachment_maintenance_role_hosts_the_repair_round_and_nothing_that_answers_a_caller()
    {
        HostApplicationBuilder builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Worker:Role"] = "attachment-maintenance",
            ["Modules:AttachmentManagement:Persistence:Ef:ConnectionString"] =
                "Host=localhost;Database=worker_tests;Username=test",
        });

        WorkerRoleCatalog.Register(builder.Services, builder.Configuration);
        using IHost host = builder.Build();

        IHostedService[] hosted = [.. host.Services.GetServices<IHostedService>()];
        hosted.OfType<AttachmentReconciliationService>().ShouldHaveSingleItem();
        hosted
            .Where(service => service is not AttachmentReconciliationService)
            .ShouldAllBe(service => service.GetType().Namespace!
                .StartsWith("Microsoft.", StringComparison.Ordinal));

        using IServiceScope scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AttachmentReconciliationScan>().ShouldNotBeNull();

        // No object store is configured here, and the composition answers with
        // a custody that claims nothing and an inventory that lists nothing,
        // rather than with a store pointed at whatever the default credential
        // chain would find.
        scope.ServiceProvider.GetRequiredService<IAttachmentObjectInventory>()
            .ShouldBeOfType<UnavailableAttachmentObjectStore>();
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
