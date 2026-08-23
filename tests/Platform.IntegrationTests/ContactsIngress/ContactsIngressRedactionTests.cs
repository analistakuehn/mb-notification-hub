using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.ContactsIngress;

/// <summary>
/// Every record of this topic carries an e-mail address or a phone number in
/// the clear by construction, and the dead-letter topic keeps records fourteen
/// times longer than the entry topic. What lands there is therefore a summary
/// rebuilt from an allow-list, never the refused body.
/// </summary>
[Collection(ContactsIngressCollectionDefinition.Name)]
public sealed class ContactsIngressRedactionTests(ContactsIngressFixture fixture)
{
    private const string Email = "redigido@example.com";
    private const string Phone = "+5511977776666";

    /// <summary>The digits of <see cref="Phone"/>; the envelope escapes the plus sign.</summary>
    private const string PhoneDigits = "5511977776666";

    [RequiresDockerFact]
    public async Task The_dead_letter_body_carries_the_diagnosis_and_no_contact_value()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();

        // A channel outside the vocabulary: the shared validator refuses the
        // shape, and the body that reaches the dead letter is a real one, with
        // real contact values in it.
        var body = ContactsIngressRecords.ContactPointsDeclaredEvent(
            recipientId,
            [
                ContactsIngressRecords.ContactPoint("email", Email),
                ContactsIngressRecords.ContactPoint("pombo-correio", Phone),
            ]);
        body.ShouldContain(Email);
        body.ShouldContain(PhoneDigits);

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture, provider, recipientId, body);

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(ContactIngestionRejectionReasons.PayloadInvalid);

        ConsumeResult<string, byte[]> record = ContactsIngressRecords.DeadLetterOf(fixture, recipientId);
        var published = ContactsIngressRecords.Body(record);

        // Neither the value nor its keyed hash: the hash is deterministic, so
        // publishing it would hand out a stable correlatable pseudonym.
        published.ShouldNotContain(Email);
        published.ShouldNotContain(PhoneDigits);
        published.ShouldNotContain("valueHash");

        JsonElement summary = JsonDocument.Parse(published).RootElement;
        summary.GetProperty("reason").GetString()
            .ShouldBe(ContactIngestionRejectionReasons.PayloadInvalid);
        summary.GetProperty("eventType").GetString()
            .ShouldBe(ContactsIngressRecords.ContactPointsDeclaredType);
        summary.GetProperty("eventSource").GetString().ShouldBe(ContactsIngressFixture.AcceptedSource);
        summary.GetProperty("contactPointCount").GetInt32().ShouldBe(2);
        summary.GetProperty("contactPointChannels")
            .EnumerateArray()
            .Select(channel => channel.GetString())
            .ShouldBe(["email", "pombo-correio"]);

        ContactsIngressRecords.Header(record, DeadLetterHeaders.Redacted).ShouldBe("true");
        ContactsIngressRecords.Header(record, DeadLetterHeaders.SourceTopic)
            .ShouldBe(ContactsIngressFixture.ContactsTopic);
        ContactsIngressRecords.Header(record, ContactIngestionDeadLetterWriter.EventTypeHeader)
            .ShouldBe(ContactsIngressRecords.ContactPointsDeclaredType);
        ContactsIngressRecords.Header(record, ContactIngestionDeadLetterWriter.EventIdHeader)
            .ShouldNotBeNullOrEmpty();

        // The headers of the notification contract have no meaning here.
        ContactsIngressRecords.Header(record, "application").ShouldBeNull();
        ContactsIngressRecords.Header(record, "idempotencyKey").ShouldBeNull();
    }

    [RequiresDockerFact]
    public async Task A_refused_consent_declaration_keeps_its_entries_in_the_summary()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();

        // No profile at all: the recipient is unknown, and the summary is what
        // tells the emitting team which entries it tried to declare.
        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            ContactsIngressRecords.ConsentsDeclaredEvent(
                recipientId,
                [ContactsIngressRecords.Consent(
                    "marketing", "email", granted: false, source: "atendimento", termsVersion: "v7")]));

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(ContactIngestionRejectionReasons.RecipientUnknown);

        ConsumeResult<string, byte[]> record = ContactsIngressRecords.DeadLetterOf(fixture, recipientId);
        JsonElement entry = JsonDocument.Parse(ContactsIngressRecords.Body(record))
            .RootElement
            .GetProperty("consents")[0];
        entry.GetProperty("purpose").GetString().ShouldBe("marketing");
        entry.GetProperty("channel").GetString().ShouldBe("email");
        entry.GetProperty("granted").GetBoolean().ShouldBeFalse();
        entry.GetProperty("source").GetString().ShouldBe("atendimento");
        entry.GetProperty("termsVersion").GetString().ShouldBe("v7");
    }
}
