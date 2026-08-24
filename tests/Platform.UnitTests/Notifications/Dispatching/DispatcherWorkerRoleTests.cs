using Microsoft.Extensions.Configuration;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications;

namespace NotificationHub.UnitTests.Notifications.Dispatching;

public sealed class DispatcherWorkerRoleTests
{
    [Fact]
    public void Without_configuration_the_bindings_cover_every_hosted_channel_and_band()
    {
        SqsQueueBinding[] bindings = DispatcherWorkerRole.QueueBindings(Configuration());

        bindings.Select(binding => binding.QueueName).ShouldBe(
        [
            "dispatch-email-auth", "dispatch-sms-auth", "dispatch-push-auth",
            "dispatch-email-critical", "dispatch-sms-critical", "dispatch-push-critical",
            "dispatch-email-transactional", "dispatch-sms-transactional", "dispatch-push-transactional",
            "dispatch-email-operational", "dispatch-sms-operational", "dispatch-push-operational",
        ]);
    }

    [Fact]
    public void The_slot_rank_follows_the_band_priority_never_the_channel()
    {
        SqsQueueBinding[] bindings = DispatcherWorkerRole.QueueBindings(Configuration());

        bindings.Where(binding => binding.QueueName.EndsWith("-auth", StringComparison.Ordinal))
            .ShouldAllBe(binding => binding.PriorityRank == 0);
        bindings.Where(binding => binding.QueueName.EndsWith("-critical", StringComparison.Ordinal))
            .ShouldAllBe(binding => binding.PriorityRank == 1);
        bindings.Where(binding => binding.QueueName.EndsWith("-transactional", StringComparison.Ordinal))
            .ShouldAllBe(binding => binding.PriorityRank == 2);
        bindings.Where(binding => binding.QueueName.EndsWith("-operational", StringComparison.Ordinal))
            .ShouldAllBe(binding => binding.PriorityRank == 3);
    }

    [Fact]
    public void A_restricted_instance_drains_only_the_configured_channel_and_bands()
    {
        SqsQueueBinding[] bindings = DispatcherWorkerRole.QueueBindings(Configuration(
            ("Modules:Notifications:Dispatcher:Channels:0", "email"),
            ("Modules:Notifications:Dispatcher:Bands:0", "auth"),
            ("Modules:Notifications:Dispatcher:Bands:1", "critical")));

        bindings.Select(binding => binding.QueueName).ShouldBe(
            ["dispatch-email-auth", "dispatch-email-critical"]);
    }

    [Fact]
    public void An_unknown_band_name_refuses_to_boot()
        => Should.Throw<InvalidOperationException>(() => DispatcherWorkerRole.QueueBindings(
                Configuration(("Modules:Notifications:Dispatcher:Bands:0", "premium"))))
            .Message.ShouldContain("premium");

    [Fact]
    public void An_unknown_channel_name_refuses_to_boot()
        => Should.Throw<InvalidOperationException>(() => DispatcherWorkerRole.QueueBindings(
                Configuration(("Modules:Notifications:Dispatcher:Channels:0", "pombo"))))
            .Message.ShouldContain("pombo");

    [Fact]
    public void A_channel_without_a_hosted_adapter_refuses_to_boot()
        => Should.Throw<InvalidOperationException>(() => DispatcherWorkerRole.QueueBindings(
                Configuration(("Modules:Notifications:Dispatcher:Channels:0", "whatsapp"))))
            .Message.ShouldContain("adapter");

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(
                setting => setting.Key, setting => (string?)setting.Value))
            .Build();
}
