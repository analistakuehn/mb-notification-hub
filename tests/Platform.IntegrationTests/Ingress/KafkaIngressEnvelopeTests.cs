using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

/// <summary>
/// The envelope type is the schema version of this topic. Accepting any type
/// whose body happens to bind would let a later version through on the
/// coincidence of field names, which is exactly the failure a versioned type
/// exists to prevent.
/// </summary>
[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class KafkaIngressEnvelopeTests(KafkaIngressFixture fixture)
{
    private const string Producer = "kyc-service";

    [RequiresDockerFact]
    public async Task An_unsupported_event_type_is_refused_before_the_body_binds()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        await using ServiceProvider provider = fixture.BuildIngressProvider();
        var recipientId = $"cus_{Guid.NewGuid():N}";

        // The same idempotency key on both records, on purpose: the two bodies
        // are identical and only the declared type moves.
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();

        KafkaDisposition unsupported = await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            KafkaIngressApi.RequestedEvent(
                application, templateKey, "transactional", recipientId, idempotencyKey,
                eventType: "araia.notification.requested.v2"),
            KafkaIngressApi.ProducerHeaders(Producer));

        unsupported.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(NotificationRejectionReasons.EventTypeUnsupported);
        (await CountNotificationsAsync(application, idempotencyKey)).ShouldBe(0);

        // Falsification: the same body under the declared type is accepted. A
        // consumer that had bound the record above would answer this one as a
        // duplicate instead, so both assertions flip together.
        KafkaDisposition supported = await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            KafkaIngressApi.RequestedEvent(
                application, templateKey, "transactional", recipientId, idempotencyKey),
            KafkaIngressApi.ProducerHeaders(Producer));

        supported.ShouldBeOfType<KafkaDisposition.Processed>();
        (await CountNotificationsAsync(application, idempotencyKey)).ShouldBe(1);
    }

    private Task<int> CountNotificationsAsync(string application, string idempotencyKey)
        => fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(notification => notification.Application == application
                && notification.IdempotencyKey == idempotencyKey));
}
