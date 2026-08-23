using System.Data.Common;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.ContactsIngress;

/// <summary>
/// Deduplication here is a single layer: no unique business key sits behind
/// the mark, so the mark has to commit inside the transaction of the effect it
/// guards, and a refusal has to reach the dead-letter topic before the mark
/// commits at all.
/// </summary>
[Collection(ContactsIngressCollectionDefinition.Name)]
public sealed class ContactsIngressDedupeTests(ContactsIngressFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_redelivery_of_the_same_record_writes_no_second_effect_and_no_second_trail()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();
        var body = ContactsIngressRecords.ContactPointsDeclaredEvent(
            recipientId,
            [ContactsIngressRecords.ContactPoint("email", "reentrega@example.com")]);

        TopicPartitionOffset position = await fixture.ProduceAsync(
            ContactsIngressFixture.ContactsTopic, recipientId, body);
        KafkaMessageContext context = ContactsIngressRecords.Context(position, recipientId, body);

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition first = await ContactsIngressRecords.SettleAsync(provider, context);

        // The very same coordinates a rebalance would redeliver.
        KafkaDisposition redelivered = await ContactsIngressRecords.SettleAsync(provider, context);

        first.ShouldBeOfType<KafkaDisposition.Processed>();
        redelivered.ShouldBeOfType<KafkaDisposition.Duplicate>();
        (await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .CountAsync(point => point.RecipientId == recipientId)))
            .ShouldBe(1);

        // The trail is what the mark protects: the declarative no-op of a
        // second delivery would append an entry to a hash-chained trail.
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.EntityId == recipientId
                && entry.Action == "contact.points.declared")))
            .ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_failure_in_the_effect_leaves_no_deduplication_mark()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();
        var body = ContactsIngressRecords.ContactPointsDeclaredEvent(
            recipientId,
            [ContactsIngressRecords.ContactPoint("email", "transacao@example.com")]);

        TopicPartitionOffset position = await fixture.ProduceAsync(
            ContactsIngressFixture.ContactsTopic, recipientId, body);
        KafkaMessageContext context = ContactsIngressRecords.Context(position, recipientId, body);

        // The audit append is the last step inside the transaction of the
        // effect. Failing it must undo everything the transaction holds,
        // including the mark.
        await using ServiceProvider provider = fixture.BuildIngressProvider(
            replaceServices: services =>
            {
                services.RemoveAll<IAuditTrail>();
                services.AddSingleton<IAuditTrail>(new FailingAuditTrail());
            });

        await Should.ThrowAsync<InvalidOperationException>(
            () => ContactsIngressRecords.SettleAsync(provider, context));

        (await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(mark => mark.MessageId == context.DedupeId)))
            .ShouldBeFalse();
        (await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .AnyAsync(point => point.RecipientId == recipientId)))
            .ShouldBeFalse();
        (await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .AnyAsync(message => message.MessageKey == recipientId)))
            .ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task The_dead_letter_record_exists_before_anything_marks_the_record_as_handled()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();
        var body = UnsupportedTypeEvent(recipientId);

        TopicPartitionOffset position = await fixture.ProduceAsync(
            ContactsIngressFixture.ContactsTopic, recipientId, body);
        KafkaMessageContext context = ContactsIngressRecords.Context(position, recipientId, body);

        // The mark is forced to fail. Whatever the processor did before it
        // must already be durable, and what it does after must not.
        await using ServiceProvider provider = fixture.BuildIngressProvider(
            replaceServices: services =>
            {
                services.RemoveAll<IProcessedMessageStore>();
                services.AddSingleton<IProcessedMessageStore>(new FailingMarkStore());
            });

        await Should.ThrowAsync<InvalidOperationException>(
            () => ContactsIngressRecords.SettleAsync(provider, context));

        // Produced first: a mark written ahead of it would make the replay of
        // a crash skip a record nobody ever recorded.
        ContactsIngressRecords.Header(
            ContactsIngressRecords.DeadLetterOf(fixture, recipientId), DeadLetterHeaders.Reason)
            .ShouldBe(ContactIngestionRejectionReasons.EventTypeUnsupported);
        (await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(mark => mark.MessageId == context.DedupeId)))
            .ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task A_redelivered_refusal_settles_as_a_duplicate_and_marks_only_once()
    {
        var recipientId = ContactsIngressRecords.NewRecipientId();
        var body = UnsupportedTypeEvent(recipientId);

        TopicPartitionOffset position = await fixture.ProduceAsync(
            ContactsIngressFixture.ContactsTopic, recipientId, body);
        KafkaMessageContext context = ContactsIngressRecords.Context(position, recipientId, body);

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition refused = await ContactsIngressRecords.SettleAsync(provider, context);
        KafkaDisposition redelivered = await ContactsIngressRecords.SettleAsync(provider, context);

        refused.ShouldBeOfType<KafkaDisposition.DeadLetter>();

        // The dead-letter record is produced before the mark is read, so a
        // redelivery does write a second one: the topic is at-least-once like
        // every other. What the mark protects is the state and the trail, and
        // the settlement tells the consumer no effect happened.
        redelivered.ShouldBeOfType<KafkaDisposition.Duplicate>();
        (await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
            .AsNoTracking()
            .CountAsync(mark => mark.MessageId == context.DedupeId)))
            .ShouldBe(1);
    }

    private static string UnsupportedTypeEvent(string recipientId)
        => ContactsIngressRecords.Envelope(
            "araia.contact.something_else.v1",
            recipientId,
            ContactsIngressFixture.AcceptedSource,
            eventId: null,
            data: new { contactPoints = Array.Empty<object>() });

    /// <summary>Turns the deduplication mark into the failure point of the settlement.</summary>
    private sealed class FailingMarkStore : IProcessedMessageStore
    {
        public Task<bool> TryMarkAsync(
            DbTransaction transaction,
            string messageId,
            string consumer,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Falha induzida ao gravar a marca de dedupe.");
    }

    private sealed class FailingAuditTrail : IAuditTrail
    {
        public Task AppendAsync(DbTransaction transaction, AuditEntry entry, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Falha induzida no append da trilha de auditoria.");

        public Task RecordApprovalAsync(
            DbTransaction transaction,
            ApprovalGrant grant,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Falha induzida no registro de aprovação.");
    }
}
