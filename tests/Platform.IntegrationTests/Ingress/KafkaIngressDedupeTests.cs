using System.Data.Common;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Features.Ingress;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class KafkaIngressDedupeTests(KafkaIngressFixture fixture)
{
    private const string Producer = "dedupe-service";

    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    [RequiresDockerFact]
    public async Task A_redelivery_of_the_same_record_settles_as_a_duplicate_without_a_second_effect()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = KafkaIngressApi.RequestedEvent(
            application, templateKey, "transactional", recipientId, idempotencyKey);

        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic, recipientId, body,
            KafkaIngressApi.ProducerHeaders(Producer));
        KafkaMessageContext context = IngressRecords.Context(
            position, recipientId, body, KafkaIngressApi.ProducerHeaders(Producer));

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition first = await SettleAsync(provider, context);
        // The very same coordinates a rebalance would redeliver.
        KafkaDisposition redelivered = await SettleAsync(provider, context);

        first.ShouldBeOfType<KafkaDisposition.Processed>();
        redelivered.ShouldBeOfType<KafkaDisposition.Duplicate>();
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(notification => notification.Application == application
                && notification.IdempotencyKey == idempotencyKey)))
            .ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task The_dead_letter_record_exists_before_anything_marks_the_event_as_handled()
    {
        var application = KafkaIngressApi.NewApplication();
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = KafkaIngressApi.RequestedEvent(
            application, "template-that-was-never-published", "transactional", recipientId, idempotencyKey);

        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic, recipientId, body,
            KafkaIngressApi.ProducerHeaders(Producer));
        KafkaMessageContext context = IngressRecords.Context(
            position, recipientId, body, KafkaIngressApi.ProducerHeaders(Producer));

        // The database commit is forced to fail. Whatever the processor did
        // before it must already be durable, and what it does after must not.
        await using ServiceProvider provider = fixture.BuildIngressProvider(
            replaceServices: services => services.AddSingleton<IProcessedMessageStore, FailingMarkStore>());

        await Should.ThrowAsync<InvalidOperationException>(() => SettleAsync(provider, context));

        // Produced first: a mark written ahead of it would make the replay of
        // a crash skip a record nobody ever recorded.
        fixture.ReadAll(KafkaIngressFixture.DeadLetterTopic, ReadBudget)
            .ShouldContain(record => IngressRecords.Header(record, "idempotencyKey") == idempotencyKey);
        (await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(mark => mark.MessageId == context.DedupeId)))
            .ShouldBeFalse();

        // Nothing of the trail committed either: the transaction that carries
        // the mark carries the audit entry too, so both roll back together.
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .AnyAsync(entry => entry.EntityId == $"{application}:{idempotencyKey}")))
            .ShouldBeFalse();
    }

    private static async Task<KafkaDisposition> SettleAsync(
        ServiceProvider provider,
        KafkaMessageContext context)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<KafkaIngressProcessor>()
            .ProcessAsync(context, CancellationToken.None);
    }

    /// <summary>Turns the deduplication mark into the failure point of the transaction.</summary>
    private sealed class FailingMarkStore : IProcessedMessageStore
    {
        public Task<bool> TryMarkAsync(
            DbTransaction transaction,
            string messageId,
            string consumer,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Falha induzida ao gravar a marca de dedupe.");
    }
}
