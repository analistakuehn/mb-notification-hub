using NotificationHub.Api.Composition;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Events;

namespace NotificationHub.Api.Modules.ContactConsent;

/// <summary>
/// Composition of the <c>contact-consent</c> worker role, owned by this
/// module: the consumer of the contacts-changed queue that keeps the
/// recipient snapshot cache honest by marking entries stale on every write
/// this module commits.
/// </summary>
public sealed class ContactConsentWorkerRole : IWorkerRoleModule
{
    public static string Role => "contact-consent";

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddPlatformMessaging(configuration);
        services.AddSqsMessageConsuming(configuration);
        services.AddEnvelopeEncryption(configuration);
        services.AddContactConsentReadSurface(configuration);

        services.AddScoped<IPoisonMessageSink, ContactConsentPoisonMessageSink>();
        services.AddAuditTrailSurface();
        services.AddSqsQueueConsumer<ContactsChangedProcessor>(
            [new SqsQueueBinding(ContactConsentEvents.Destination, PriorityRank: 0)]);
    }
}
