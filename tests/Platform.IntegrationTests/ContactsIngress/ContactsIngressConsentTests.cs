using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.IntegrationTests.ContactConsent;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.ContactsIngress;

/// <summary>
/// Consent declared over the bus reaches the same append-only ledger the REST
/// route writes, and every record actually appended is announced on the
/// outgoing topic of the hub. A declaration that changes nothing appends
/// nothing and announces nothing.
/// </summary>
[Collection(ContactsIngressCollectionDefinition.Name)]
public sealed class ContactsIngressConsentTests(ContactsIngressFixture fixture)
{
    private const string OutgoingTopic = "notifications.events.v1";
    private const string ConsentChangedType = "araia.notification.consent_changed.v1";

    [RequiresDockerFact]
    public async Task A_declared_consent_appends_to_the_ledger_and_announces_the_change()
    {
        var recipientId = await SeedContactAsync();

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            ContactsIngressRecords.ConsentsDeclaredEvent(
                recipientId,
                [ContactsIngressRecords.Consent("marketing", "email", granted: true, source: "importacao")]));

        disposition.ShouldBeOfType<KafkaDisposition.Processed>();

        List<bool> ledger = await LedgerAsync(recipientId);
        ledger.Count.ShouldBe(1);
        ledger[0].ShouldBeTrue();

        var announced = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.MessageKey == recipientId && message.EventType == ConsentChangedType)
            .Select(message => new { message.Destination, message.Transport, message.PayloadJson })
            .SingleAsync());
        announced.Destination.ShouldBe(OutgoingTopic);
        announced.Transport.ShouldBe("kafka");

        JsonElement envelope = JsonDocument.Parse(announced.PayloadJson).RootElement;
        envelope.GetProperty("type").GetString().ShouldBe(ConsentChangedType);
        envelope.GetProperty("subject").GetString().ShouldBe(recipientId);
        JsonElement data = envelope.GetProperty("data");
        data.GetProperty("recipientId").GetString().ShouldBe(recipientId);
        data.GetProperty("channel").GetString().ShouldBe("email");
        data.GetProperty("purpose").GetString().ShouldBe("marketing");
        data.GetProperty("granted").GetBoolean().ShouldBeTrue();
        data.GetProperty("source").GetString().ShouldBe("importacao");

        // The internal invalidation still rides the same transaction; the two
        // messages are different contracts, not one duplicated.
        (await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId
                && message.EventType == "consent.changed")))
            .ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task An_identical_declaration_appends_nothing_and_announces_nothing()
    {
        var recipientId = await SeedContactAsync();
        object[] consents = [ContactsIngressRecords.Consent("marketing", "email", granted: true)];

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        (await ContactsIngressRecords.ProcessAsync(
            fixture, provider, recipientId,
            ContactsIngressRecords.ConsentsDeclaredEvent(recipientId, consents)))
            .ShouldBeOfType<KafkaDisposition.Processed>();

        // A different record carrying the same desired state: the offset mark
        // does not apply, so the no-op is the declarative semantics answering.
        KafkaDisposition repeated = await ContactsIngressRecords.ProcessAsync(
            fixture, provider, recipientId,
            ContactsIngressRecords.ConsentsDeclaredEvent(recipientId, consents));

        repeated.ShouldBeOfType<KafkaDisposition.Processed>();
        (await LedgerAsync(recipientId)).Count.ShouldBe(1);
        (await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId
                && message.EventType == ConsentChangedType)))
            .ShouldBe(1);

        // The declaration that changed nothing still leaves its trail.
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.EntityId == recipientId && entry.Action == "consents.declared")))
            .ShouldBe(2);
    }

    [RequiresDockerFact]
    public async Task A_revocation_appends_a_new_record_and_announces_it_as_not_granted()
    {
        var recipientId = await SeedContactAsync();

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        (await ContactsIngressRecords.ProcessAsync(
            fixture, provider, recipientId,
            ContactsIngressRecords.ConsentsDeclaredEvent(
                recipientId,
                [ContactsIngressRecords.Consent("marketing", "email", granted: true)])))
            .ShouldBeOfType<KafkaDisposition.Processed>();

        (await ContactsIngressRecords.ProcessAsync(
            fixture, provider, recipientId,
            ContactsIngressRecords.ConsentsDeclaredEvent(
                recipientId,
                [ContactsIngressRecords.Consent(
                    "marketing", "email", granted: false, source: "atendimento")])))
            .ShouldBeOfType<KafkaDisposition.Processed>();

        List<bool> ledger = await LedgerAsync(recipientId);
        ledger.Count.ShouldBe(2);

        List<string> payloads = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.MessageKey == recipientId && message.EventType == ConsentChangedType)
            .Select(message => message.PayloadJson)
            .ToListAsync());
        List<bool> announced =
        [
            .. payloads.Select(payload => JsonDocument.Parse(payload).RootElement
                .GetProperty("data").GetProperty("granted").GetBoolean()),
        ];
        announced.Count.ShouldBe(2);
        announced.Count(granted => granted).ShouldBe(1);
        announced.Count(granted => !granted).ShouldBe(1);
    }

    private async Task<string> SeedContactAsync()
    {
        HttpClient writer = fixture.CreateWriterClient("contacts-writer");
        var recipientId = ContactsIngressRecords.NewRecipientId();
        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", $"{recipientId}@example.com")])))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return recipientId;
    }

    private Task<List<bool>> LedgerAsync(string recipientId)
        => fixture.QueryContactConsentDbAsync(db => db.Consents
            .AsNoTracking()
            .Join(
                db.ContactPoints.AsNoTracking().Where(point => point.RecipientId == recipientId),
                consent => consent.ContactPointId,
                point => point.Id,
                (consent, point) => consent.Granted)
            .ToListAsync());
}
