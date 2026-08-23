using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.IntegrationTests.ContactConsent;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.ContactsIngress;

/// <summary>
/// Each outcome of the use cases settles the record the way the transport can
/// act on it: what can never be applied goes to the dead-letter topic with a
/// reason the emitting team can fix, and what a later attempt would apply
/// cleanly holds the partition instead.
/// </summary>
[Collection(ContactsIngressCollectionDefinition.Name)]
public sealed class ContactsIngressOutcomeTests(ContactsIngressFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_consent_for_an_unknown_recipient_is_refused_as_such()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            ContactsIngressRecords.ConsentsDeclaredEvent(
                recipientId,
                [ContactsIngressRecords.Consent("marketing", "email", granted: true)]));

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(ContactIngestionRejectionReasons.RecipientUnknown);
    }

    [RequiresDockerFact]
    public async Task A_consent_for_a_channel_without_a_contact_point_is_refused_as_such()
    {
        var recipientId = await SeedEmailContactAsync();

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            ContactsIngressRecords.ConsentsDeclaredEvent(
                recipientId,
                [ContactsIngressRecords.Consent("marketing", "whatsapp", granted: true)]));

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(ContactIngestionRejectionReasons.NoContactPointForChannel);
    }

    [RequiresDockerFact]
    public async Task An_event_type_this_hub_does_not_consume_is_refused_as_unsupported()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();
        var body = ContactsIngressRecords.Envelope(
            "araia.contact.device_registered.v1",
            recipientId,
            ContactsIngressFixture.AcceptedSource,
            eventId: null,
            data: new { token = "fcm-token" });

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture, provider, recipientId, body);

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(ContactIngestionRejectionReasons.EventTypeUnsupported);
    }

    [RequiresDockerFact]
    public async Task A_declaration_without_the_declared_set_is_refused_and_removes_nothing()
    {
        var recipientId = await SeedEmailContactAsync();
        var body = ContactsIngressRecords.Envelope(
            ContactsIngressRecords.ContactPointsDeclaredType,
            recipientId,
            ContactsIngressFixture.AcceptedSource,
            eventId: null,
            data: new { timezone = "America/Sao_Paulo" });

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture, provider, recipientId, body);

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(ContactIngestionRejectionReasons.PayloadInvalid);

        // An absent collection is not an empty declaration: reading it as one
        // would remove every contact point on behalf of a producer that said
        // nothing about them.
        (await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .CountAsync(point => point.RecipientId == recipientId && point.RemovedAt == null)))
            .ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_record_without_a_subject_is_refused_as_an_invalid_payload()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();
        var body = ContactsIngressRecords.Envelope(
            ContactsIngressRecords.ContactPointsDeclaredType,
            recipientId: string.Empty,
            source: ContactsIngressFixture.AcceptedSource,
            eventId: null,
            data: new { contactPoints = new[] { ContactsIngressRecords.ContactPoint("email", "sem@example.com") } });

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture, provider, recipientId, body);

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(ContactIngestionRejectionReasons.PayloadInvalid);
    }

    [RequiresDockerFact]
    public async Task A_concurrent_write_that_wins_the_race_holds_the_record_for_a_retry()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();

        // The mark runs first inside the transaction of the effect, so it is
        // the seam where a competing write can land before this one saves.
        await using ServiceProvider provider = fixture.BuildIngressProvider(
            replaceServices: services =>
            {
                services.RemoveAll<IProcessedMessageStore>();
                services.AddSingleton<IProcessedMessageStore>(
                    new RacingMarkStore(() => CreateProfileAsync(recipientId)));
            });

        KafkaDisposition disposition = await ContactsIngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            ContactsIngressRecords.ContactPointsDeclaredEvent(
                recipientId,
                [ContactsIngressRecords.ContactPoint("email", "corrida@example.com")]));

        // Retry, never dead letter: the same record applies cleanly once the
        // competing write is visible, and nothing was committed here.
        disposition.ShouldBeOfType<KafkaDisposition.Retry>();
        (await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .AnyAsync(point => point.RecipientId == recipientId)))
            .ShouldBeFalse();
    }

    private async Task<string> SeedEmailContactAsync()
    {
        HttpClient writer = fixture.CreateWriterClient("contacts-writer");
        var recipientId = ContactsIngressRecords.NewRecipientId();
        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", $"{recipientId}@example.com")])))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return recipientId;
    }

    /// <summary>The competing write: another declaration creates the profile row first.</summary>
    private async Task CreateProfileAsync(string recipientId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        ContactConsentDbContext db = scope.ServiceProvider.GetRequiredService<ContactConsentDbContext>();
        db.RecipientProfiles.Add(RecipientProfile.Create(recipientId, null, null, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Lets one competing write commit between the deduplication mark and the
    /// save of the effect, which is exactly the window a second declaration of
    /// the same recipient occupies in production.
    /// </summary>
    private sealed class RacingMarkStore(Func<Task> race) : IProcessedMessageStore
    {
        private readonly PostgresProcessedMessageStore _inner = new(TimeProvider.System);
        private int _raced;

        public async Task<bool> TryMarkAsync(
            DbTransaction transaction,
            string messageId,
            string consumer,
            CancellationToken cancellationToken)
        {
            var marked = await _inner.TryMarkAsync(transaction, messageId, consumer, cancellationToken);
            if (Interlocked.Exchange(ref _raced, 1) == 0)
            {
                await race();
            }

            return marked;
        }
    }
}
