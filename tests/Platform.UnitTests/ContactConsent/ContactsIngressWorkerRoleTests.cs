using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.ContactConsent;
using NotificationHub.Api.Modules.ContactConsent.Features.Ingress;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Reads;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;

namespace NotificationHub.UnitTests.ContactConsent;

/// <summary>
/// The ingestion role is a write deployment of its own: it hosts the consumer
/// and the two use cases, and nothing of the read side, whose cache and Redis
/// belong to whoever reads.
/// </summary>
public sealed class ContactsIngressWorkerRoleTests
{
    private const string AcceptedSource = "urn:araia:cadastro";

    [Fact]
    public void The_role_hosts_the_bus_consumer_and_the_two_declaration_use_cases()
    {
        var services = new ServiceCollection();

        ContactsIngressWorkerRole.ConfigureServices(services, Configuration(AcceptedSource));

        Registers(services, typeof(ContactsIngressProcessor)).ShouldBeTrue();
        Registers(services, typeof(ContactDeclarationApplier)).ShouldBeTrue();
        Registers(services, typeof(ContactIngestionDeadLetterWriter)).ShouldBeTrue();
        ContactsIngressWorkerRole.Role.ShouldBe("contacts-ingress");
    }

    [Fact]
    public void The_role_composes_no_read_surface()
    {
        var services = new ServiceCollection();

        ContactsIngressWorkerRole.ConfigureServices(services, Configuration(AcceptedSource));

        // The ingestion writes; the snapshot cache and its Redis exist for the
        // role that reads, and hosting them here would put a second consumer
        // of a different scaling signal in the same deployment.
        Registers(services, typeof(IRecipientDirectory)).ShouldBeFalse();
        Registers(services, typeof(RecipientSnapshotCache)).ShouldBeFalse();
    }

    [Fact]
    public void A_role_without_accepted_sources_refuses_to_boot()
    {
        ServiceProvider provider = Build(Configuration());

        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain(nameof(ContactsIngressOptions.AcceptedSources));
    }

    [Fact]
    public void One_accepted_source_is_enough_to_boot()
    {
        // Falsification of the refusal above: everything else in the
        // configuration is identical, so the refusal is measuring the empty
        // list and not a missing connection string.
        ServiceProvider provider = Build(Configuration(AcceptedSource));

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
        provider.GetRequiredService<IOptions<ContactsIngressOptions>>().Value
            .AcceptedSources.ShouldBe([AcceptedSource]);
    }

    private static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        ContactsIngressWorkerRole.ConfigureServices(services, configuration);
        return services.BuildServiceProvider();
    }

    private static bool Registers(IServiceCollection services, Type serviceType)
        => services.Any(descriptor => descriptor.ServiceType == serviceType);

    private static IConfiguration Configuration(params string[] acceptedSources)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Platform:Messaging:Ef:ConnectionString"] = "Host=localhost;Database=hub;Username=postgres",
            ["Platform:Messaging:KafkaConsumer:BootstrapServers"] = "localhost:9092",
            ["Platform:Cryptography:Envelope:KeyId"] = "unit-tests-envelope",
            ["Platform:Cryptography:Envelope:MasterKey"] = Convert.ToBase64String(new byte[32]),
            ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] =
                "Host=localhost;Database=hub;Username=postgres",
        };
        for (var index = 0; index < acceptedSources.Length; index++)
        {
            settings[$"{ContactsIngressOptions.SectionName}:AcceptedSources:{index}"] = acceptedSources[index];
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
