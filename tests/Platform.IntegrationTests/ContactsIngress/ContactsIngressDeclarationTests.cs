using System.Net;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.IntegrationTests.ContactConsent;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.ContactsIngress;

/// <summary>
/// The declarative semantics of the write surface, exercised over the bus:
/// the event carries the complete set of contact points of the recipient,
/// because the registration system owns them, so what the declaration omits
/// stops being addressable.
/// </summary>
[Collection(ContactsIngressCollectionDefinition.Name)]
public sealed class ContactsIngressDeclarationTests(ContactsIngressFixture fixture)
{
    private const string Phone = "+5511999990000";

    [RequiresDockerFact]
    public async Task A_declared_set_adds_the_new_contact_point_and_removes_the_one_it_omits()
    {
        HttpClient writer = fixture.CreateWriterClient("contacts-writer");
        var recipientId = ContactsIngressRecords.NewRecipientId();
        const string OldEmail = "antigo@example.com";
        const string NewEmail = "novo@example.com";
        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody(
            [
                ContactConsentApi.ContactPoint("email", OldEmail),
                ContactConsentApi.ContactPoint("sms", Phone),
            ]))).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            ContactsIngressRecords.ContactPointsDeclaredEvent(
                recipientId,
                [
                    ContactsIngressRecords.ContactPoint("email", NewEmail),
                    ContactsIngressRecords.ContactPoint("sms", Phone),
                ],
                timezone: "America/Manaus"));

        disposition.ShouldBeOfType<KafkaDisposition.Processed>();

        // The omitted value is stamped removed, never deleted: the consent
        // ledger anchors on the row.
        var rows = await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .Where(point => point.RecipientId == recipientId)
            .Select(point => new { point.Id, point.Channel, point.RemovedAt, point.ValueEncrypted })
            .ToListAsync());
        rows.Count.ShouldBe(3);
        rows.Count(row => row.Channel == "email" && row.RemovedAt != null).ShouldBe(1);
        rows.Count(row => row.Channel == "email" && row.RemovedAt == null).ShouldBe(1);
        rows.Count(row => row.Channel == "sms" && row.RemovedAt == null).ShouldBe(1);

        // The value the bus declared is sealed at rest and only opens through
        // the explicit read of the published contract.
        var declared = rows.Single(row => row.Channel == "email" && row.RemovedAt == null);
        Encoding.UTF8.GetString(declared.ValueEncrypted).ShouldNotContain(NewEmail);
        Result<string> revealed = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<IRecipientDirectory>()
            .RevealContactValueAsync(recipientId, declared.Id, CancellationToken.None));
        revealed.IsSuccess.ShouldBeTrue();
        revealed.Value.ShouldBe(NewEmail);

        (await fixture.QueryContactConsentDbAsync(db => db.RecipientProfiles
            .AsNoTracking()
            .Where(profile => profile.RecipientId == recipientId)
            .Select(profile => profile.Timezone)
            .SingleAsync()))
            .ShouldBe("America/Manaus");
    }

    [RequiresDockerFact]
    public async Task The_trail_of_a_declared_set_records_the_accepted_source_as_the_actor()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();
        var eventId = $"evt-{Guid.NewGuid():N}";
        var body = ContactsIngressRecords.ContactPointsDeclaredEvent(
            recipientId,
            [ContactsIngressRecords.ContactPoint("email", "trilha@example.com")],
            eventId: eventId);

        TopicPartitionOffset position = await fixture.ProduceAsync(
            ContactsIngressFixture.ContactsTopic, recipientId, body);
        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ContactsIngressRecords.SettleAsync(
            provider, ContactsIngressRecords.Context(position, recipientId, body));

        disposition.ShouldBeOfType<KafkaDisposition.Processed>();

        var trail = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.EntityId == recipientId && entry.Action == "contact.points.declared")
            .Select(entry => new { entry.ActorId, entry.ActorType, entry.DetailsJson })
            .SingleAsync());
        trail.ActorId.ShouldBe(ContactsIngressFixture.AcceptedSource);
        trail.ActorType.ShouldBe("system");

        // The coordinates let a disputed declaration be checked against the
        // record the broker still holds; the event id correlates it with the
        // producer's own log.
        JsonElement origin = JsonDocument.Parse(trail.DetailsJson).RootElement.GetProperty("origin");
        origin.GetProperty("record").GetString()
            .ShouldBe($"{position.Topic}:{position.Partition.Value}:{position.Offset.Value}");
        origin.GetProperty("eventId").GetString().ShouldBe(eventId);
    }

    [RequiresDockerFact]
    public async Task A_write_over_the_rest_route_keeps_a_trail_without_provenance()
    {
        HttpClient writer = fixture.CreateWriterClient("contacts-writer");
        var recipientId = ContactsIngressRecords.NewRecipientId();

        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", "rest@example.com")])))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Falsification of the assertion above: the field exists only for a
        // write that arrived as a record, so the synchronous route must not
        // grow one.
        var details = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.EntityId == recipientId && entry.Action == "contact.points.declared")
            .Select(entry => entry.DetailsJson)
            .SingleAsync());
        JsonDocument.Parse(details).RootElement.TryGetProperty("origin", out _).ShouldBeFalse();
    }
}
