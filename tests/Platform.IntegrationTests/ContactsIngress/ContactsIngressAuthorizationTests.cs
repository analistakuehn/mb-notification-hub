using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.ContactsIngress;

/// <summary>
/// The accepted-source list is the actor vocabulary of this transport: what it
/// refuses never reaches the ledger, and what it accepts writes the trail
/// under the identity of the emitting system.
/// </summary>
[Collection(ContactsIngressCollectionDefinition.Name)]
public sealed class ContactsIngressAuthorizationTests(ContactsIngressFixture fixture)
{
    private const string Intruder = "urn:araia:sistema-nao-autorizado";

    [RequiresDockerFact]
    public async Task A_source_outside_the_accepted_list_is_refused_and_writes_nothing()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();
        var body = ContactsIngressRecords.ContactPointsDeclaredEvent(
            recipientId,
            [ContactsIngressRecords.ContactPoint("email", "intruso@example.com")],
            source: Intruder);

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture, provider, recipientId, body);

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(ContactIngestionRejectionReasons.SourceNotAuthorized);
        (await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .AnyAsync(point => point.RecipientId == recipientId)))
            .ShouldBeFalse();
        (await fixture.QueryContactConsentDbAsync(db => db.RecipientProfiles
            .AsNoTracking()
            .AnyAsync(profile => profile.RecipientId == recipientId)))
            .ShouldBeFalse();

        ConsumeResult<string, byte[]> record = ContactsIngressRecords.DeadLetterOf(fixture, recipientId);
        ContactsIngressRecords.Header(record, DeadLetterHeaders.Reason)
            .ShouldBe(ContactIngestionRejectionReasons.SourceNotAuthorized);
        ContactsIngressRecords.Header(record, ContactIngestionDeadLetterWriter.EventSourceHeader)
            .ShouldBe(Intruder);

        // The body of a source this role does not accept is never read, not
        // even to summarize it.
        var published = ContactsIngressRecords.Body(record);
        published.ShouldNotContain("intruso@example.com");
        published.ShouldNotContain("contactPointChannels");
    }

    [RequiresDockerFact]
    public async Task The_same_declaration_from_an_accepted_source_is_applied()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();

        await using ServiceProvider provider = fixture.BuildIngressProvider();

        // Falsification of the refusal above: only the source differs, so the
        // refusal is measuring authorization and not a malformed record.
        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            ContactsIngressRecords.ContactPointsDeclaredEvent(
                recipientId,
                [ContactsIngressRecords.ContactPoint("email", "aceito@example.com")]));

        disposition.ShouldBeOfType<KafkaDisposition.Processed>();
        (await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .CountAsync(point => point.RecipientId == recipientId)))
            .ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_source_accepted_by_another_deployment_is_refused_by_this_one()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();

        // The list is configuration of the role: the same record is accepted
        // or refused by the deployment that reads it, never by the record.
        await using ServiceProvider provider = fixture.BuildIngressProvider(
            new Dictionary<string, string?>
            {
                ["Modules:ContactConsent:KafkaIngress:AcceptedSources:0"] = "urn:araia:outro-cadastro",
            });
        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            ContactsIngressRecords.ContactPointsDeclaredEvent(
                recipientId,
                [ContactsIngressRecords.ContactPoint("email", "configuracao@example.com")]));

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(ContactIngestionRejectionReasons.SourceNotAuthorized);
    }
}
