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

    /// <summary>
    /// The vocabulary accepts a channel name in any casing, and the hosted
    /// list spells it in one. Comparing the operator's spelling against the
    /// hosted one is what made a mixed-case list refuse to boot with the wrong
    /// diagnosis, saying the channel had no adapter when it has one. Repairing
    /// only that comparison would have been worse than the defect: the name
    /// would clear the guard and then fall out of the selection, so the
    /// instance would boot healthy draining one of the two configured channels
    /// and never say so, because a shorter binding list is not an empty one.
    /// </summary>
    [Fact]
    public void A_channel_configured_in_another_casing_is_drained_and_never_silently_dropped()
    {
        SqsQueueBinding[] bindings = DispatcherWorkerRole.QueueBindings(Configuration(
            ("Modules:Notifications:Dispatcher:Channels:0", "EMAIL"),
            ("Modules:Notifications:Dispatcher:Channels:1", "sms"),
            ("Modules:Notifications:Dispatcher:Bands:0", "auth")));

        bindings.Select(binding => binding.QueueName).ShouldBe(
            ["dispatch-email-auth", "dispatch-sms-auth"],
            "os dois canais configurados têm adapter hospedado, então os dois têm de ser drenados; "
            + "uma fila só significaria que o canal escrito em outra caixa foi descartado em "
            + "silêncio, e a contagem não zero deixaria a instância subir saudável assim mesmo.");
    }

    /// <summary>
    /// Surrounding blank space is part of the same tolerance: the vocabulary
    /// trims before it matches, so the selection has to run on what it
    /// resolved to and not on what the configuration file carried.
    /// </summary>
    [Fact]
    public void A_channel_configured_with_surrounding_space_is_drained_too()
        => DispatcherWorkerRole.QueueBindings(Configuration(
                ("Modules:Notifications:Dispatcher:Channels:0", " email "),
                ("Modules:Notifications:Dispatcher:Bands:0", "auth")))
            .Select(binding => binding.QueueName)
            .ShouldBe(["dispatch-email-auth"]);

    /// <summary>The hosting order decides the queue order, never the configured one.</summary>
    [Fact]
    public void The_drained_channels_keep_the_hosting_order_not_the_configured_one()
        => DispatcherWorkerRole.QueueBindings(Configuration(
                ("Modules:Notifications:Dispatcher:Channels:0", "SMS"),
                ("Modules:Notifications:Dispatcher:Channels:1", "Email"),
                ("Modules:Notifications:Dispatcher:Bands:0", "auth")))
            .Select(binding => binding.QueueName)
            .ShouldBe(["dispatch-email-auth", "dispatch-sms-auth"]);

    /// <summary>
    /// A channel of the vocabulary without an adapter here still refuses to
    /// boot in any casing, and it refuses for the reason that is true: the
    /// adapter is missing, not the channel.
    /// </summary>
    [Fact]
    public void A_hosted_adapter_is_still_required_whatever_the_casing()
        => Should.Throw<InvalidOperationException>(() => DispatcherWorkerRole.QueueBindings(
                Configuration(("Modules:Notifications:Dispatcher:Channels:0", "WhatsApp"))))
            .Message.ShouldContain("adapter");

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(
                setting => setting.Key, setting => (string?)setting.Value))
            .Build();
}
