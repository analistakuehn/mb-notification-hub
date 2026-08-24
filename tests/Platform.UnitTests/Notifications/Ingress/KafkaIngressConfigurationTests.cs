using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Features.Ingress;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;

namespace NotificationHub.UnitTests.Notifications.Ingress;

public sealed class KafkaIngressConfigurationTests
{
    private const string TopicA = "notifications.requested.kyc.v1";
    private const string TopicB = "notifications.requested.billing.v1";
    private const string ProducerA = "kyc-service";
    private const string ProducerB = "billing-service";

    [Fact]
    public void The_role_subscribes_every_configured_producer_topic()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        KafkaIngressWorkerRole.ConfigureServices(services, Configuration(ValidSettings()));

        using ServiceProvider provider = services.BuildServiceProvider();
        KafkaConsumerPlan<KafkaIngressProcessor> plan =
            provider.GetRequiredService<KafkaConsumerPlan<KafkaIngressProcessor>>();
        plan.Topics.ShouldBe([TopicA, TopicB]);
    }

    [Fact]
    public void The_role_registers_the_notifications_kill_switch()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        KafkaIngressWorkerRole.ConfigureServices(services, Configuration(ValidSettings()));

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IKillSwitch>().ShouldNotBeNull();
    }

    [Fact]
    public void A_legacy_requested_topic_does_not_replace_missing_bindings()
    {
        Dictionary<string, string?> settings = ValidSettings();
        RemoveBindings(settings);
        settings[$"{KafkaIngressOptions.SectionName}:RequestedTopic"] = "notifications.requested.v1";

        AssertInvalid(settings, "Bindings");
    }

    [Fact]
    public void An_empty_topic_refuses_composition()
    {
        Dictionary<string, string?> settings = ValidSettings();
        settings[$"{KafkaIngressOptions.SectionName}:Bindings:0:Topic"] = "   ";

        AssertInvalid(settings, "Topic");
    }

    [Fact]
    public void An_empty_logical_producer_refuses_composition()
    {
        Dictionary<string, string?> settings = ValidSettings();
        settings[$"{KafkaIngressOptions.SectionName}:Bindings:0:LogicalProducer"] = "   ";

        AssertInvalid(settings, "LogicalProducer");
    }

    [Fact]
    public void A_repeated_binding_refuses_composition()
    {
        Dictionary<string, string?> settings = ValidSettings();
        settings[$"{KafkaIngressOptions.SectionName}:Bindings:1:Topic"] = TopicA;
        settings[$"{KafkaIngressOptions.SectionName}:Bindings:1:LogicalProducer"] = ProducerA;

        AssertInvalid(settings, "duplicado");
    }

    [Fact]
    public void One_topic_cannot_identify_two_logical_producers()
    {
        Dictionary<string, string?> settings = ValidSettings();
        settings[$"{KafkaIngressOptions.SectionName}:Bindings:1:Topic"] = TopicA;

        AssertInvalid(settings, TopicA);
    }

    [Fact]
    public void One_logical_producer_cannot_own_two_topics()
    {
        Dictionary<string, string?> settings = ValidSettings();
        settings[$"{KafkaIngressOptions.SectionName}:Bindings:1:LogicalProducer"] = ProducerA;

        AssertInvalid(settings, ProducerA);
    }

    [Fact]
    public void An_input_topic_cannot_be_the_dead_letter_topic()
    {
        Dictionary<string, string?> settings = ValidSettings();
        settings[$"{KafkaIngressOptions.SectionName}:Bindings:0:Topic"] =
            settings[$"{KafkaIngressOptions.SectionName}:DeadLetterTopic"];

        AssertInvalid(settings, "dead-letter");
    }

    private static void AssertInvalid(Dictionary<string, string?> settings, string expectedMessage)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => KafkaIngressWorkerRole.ConfigureServices(services, Configuration(settings)));

        failure.Message.ShouldContain(expectedMessage, Case.Insensitive);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static Dictionary<string, string?> ValidSettings()
        => new(StringComparer.Ordinal)
        {
            ["Platform:Messaging:Ef:ConnectionString"] =
                "Host=localhost;Database=hub;Username=postgres",
            ["Platform:Messaging:KafkaConsumer:BootstrapServers"] = "localhost:9092",
            ["Platform:Cryptography:Envelope:KeyId"] = "unit-tests-envelope",
            ["Platform:Cryptography:Envelope:MasterKey"] = Convert.ToBase64String(new byte[32]),
            ["Modules:Notifications:Persistence:Ef:ConnectionString"] =
                "Host=localhost;Database=hub;Username=postgres",
            ["Modules:Notifications:Redis:ConnectionString"] = "localhost:6379",
            ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] =
                "Host=localhost;Database=hub;Username=postgres",
            [$"{KafkaIngressOptions.SectionName}:DeadLetterTopic"] = "notifications.requested.dlt",
            [$"{KafkaIngressOptions.SectionName}:ConsumerGroup"] = "notification-hub-ingress",
            [$"{KafkaIngressOptions.SectionName}:Bindings:0:Topic"] = TopicA,
            [$"{KafkaIngressOptions.SectionName}:Bindings:0:LogicalProducer"] = ProducerA,
            [$"{KafkaIngressOptions.SectionName}:Bindings:1:Topic"] = TopicB,
            [$"{KafkaIngressOptions.SectionName}:Bindings:1:LogicalProducer"] = ProducerB,
        };

    private static void RemoveBindings(Dictionary<string, string?> settings)
    {
        string[] bindingKeys = [.. settings.Keys.Where(
            key => key.StartsWith($"{KafkaIngressOptions.SectionName}:Bindings:", StringComparison.Ordinal))];
        foreach (var key in bindingKeys)
        {
            settings.Remove(key);
        }
    }
}
