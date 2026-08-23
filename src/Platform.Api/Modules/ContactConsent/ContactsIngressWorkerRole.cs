using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Features.Ingress;
using NotificationHub.Api.Modules.ContactConsent.Features.Mutations;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;

namespace NotificationHub.Api.Modules.ContactConsent;

/// <summary>
/// Composition of the <c>contacts-ingress</c> worker role, owned by this
/// module: the bus consumer of contact and consent declarations, the two use
/// cases that own them, and the dead-letter writer of this topic pair.
///
/// A role of its own, separate from <c>contact-consent</c>, which keeps
/// consuming the invalidation queue. The two scale on different signals,
/// consumer lag against queue depth, and a bus consumer registers a gate
/// health check for the whole process: hosting both would make a closed gate
/// take down the pod that exists to keep the cache honest.
///
/// It writes, so it does not compose the read surface: the snapshot cache and
/// its Redis belong to whoever reads, and this role never does.
/// </summary>
public sealed class ContactsIngressWorkerRole : IWorkerRoleModule
{
    public static string Role => "contacts-ingress";

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddPlatformMessaging(configuration);
        services.AddKafkaMessageConsuming(configuration);
        services.AddContactConsentPersistence(configuration);

        // The declaration carries contact values in the clear, so this role
        // needs the cipher the module seals them with.
        services.AddEnvelopeEncryption(configuration);
        services.AddAuditTrailSurface();

        services.AddOptions<ContactsIngressOptions>()
            .Bind(configuration.GetSection(ContactsIngressOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<ContactIngestionDeadLetterWriter>();

        services.AddScoped<ContactValueProtector>();
        services.AddScoped<ContactConsentWriter>();
        services.AddScoped<DeclareContactPoints.Handler>();
        services.AddScoped<DeclareConsents.Handler>();
        services.TryAddScoped<IValidator<DeclareContactPoints.Command>, DeclareContactPoints.Validator>();
        services.TryAddScoped<IValidator<DeclareConsents.Command>, DeclareConsents.Validator>();
        services.AddScoped<ContactDeclarationApplier>();

        ContactsIngressOptions ingress = configuration
            .GetSection(ContactsIngressOptions.SectionName)
            .Get<ContactsIngressOptions>() ?? new ContactsIngressOptions();
        services.AddKafkaTopicConsumer<ContactsIngressProcessor>(
            ingress.ConsumerGroup, [ingress.ContactsTopic]);
    }
}
