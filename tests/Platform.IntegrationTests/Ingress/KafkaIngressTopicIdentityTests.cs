using System.Globalization;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Ingress;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class KafkaIngressTopicIdentityTests(KafkaIngressFixture fixture)
{
    private const string ProducerHeader = "producer";

    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    [RequiresDockerFact]
    public async Task Topic_A_cannot_use_claims_for_B_to_receive_Bs_grants()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync(
            (KafkaIngressFixture.SecondaryRequestedProducer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = KafkaIngressApi.RequestedEvent(
            application,
            templateKey,
            "transactional",
            recipientId,
            idempotencyKey,
            new KafkaIngressApi.RequestedEventOptions
            {
                EventSource = $"urn:araia:{KafkaIngressFixture.SecondaryRequestedProducer}",
            });
        Dictionary<string, string> headers =
            KafkaIngressApi.ProducerHeaders(KafkaIngressFixture.SecondaryRequestedProducer);

        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic,
            recipientId,
            body,
            headers);
        KafkaMessageContext context = IngressRecords.Context(position, recipientId, body, headers);
        await using ServiceProvider provider = fixture.BuildIngressProvider();

        KafkaDisposition disposition = await ProcessAsync(provider, context);

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe("producer-not-authorized");
        ConsumeResult<string, byte[]> deadLetter = fixture
            .ReadAll(KafkaIngressFixture.DeadLetterTopic, ReadBudget)
            .Single(record => IsDeadLetterFor(record, position));
        AssertPreTrustDeadLetter(deadLetter, position, "producer-not-authorized");
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .AnyAsync(notification => notification.IdempotencyKey == idempotencyKey)))
            .ShouldBeFalse();
        (await AuditActorAsync("notification.rejected_at_ingress", $"{application}:{idempotencyKey}"))
            .ShouldBe(KafkaIngressFixture.RequestedProducer);
    }

    [RequiresDockerFact]
    public async Task Topic_A_sets_requested_by_and_the_audit_actor_from_its_binding()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync(
            (KafkaIngressFixture.RequestedProducer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = KafkaIngressApi.RequestedEvent(
            application,
            templateKey,
            "transactional",
            recipientId,
            idempotencyKey,
            new KafkaIngressApi.RequestedEventOptions
            {
                EventSource = $"urn:araia:{KafkaIngressFixture.SecondaryRequestedProducer}",
            });

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            body,
            KafkaIngressApi.ProducerHeaders(KafkaIngressFixture.SecondaryRequestedProducer));

        disposition.ShouldBeOfType<KafkaDisposition.Processed>();
        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.IdempotencyKey == idempotencyKey));
        notification.RequestedBy.ShouldBe(KafkaIngressFixture.RequestedProducer);
        (await AuditActorAsync("notification.accepted", notification.Id.ToString()))
            .ShouldBe(KafkaIngressFixture.RequestedProducer);
    }

    [RequiresDockerFact]
    public async Task An_unknown_topic_fails_before_any_business_effect()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync(
            (KafkaIngressFixture.SecondaryRequestedProducer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = KafkaIngressApi.RequestedEvent(
            application,
            templateKey,
            "transactional",
            recipientId,
            idempotencyKey,
            new KafkaIngressApi.RequestedEventOptions
            {
                EventSource = $"urn:araia:{KafkaIngressFixture.SecondaryRequestedProducer}",
            });
        var position = new TopicPartitionOffset(
            "notifications.requested.unknown.v1", new Partition(0), new Offset(0));

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        using IServiceScope scope = provider.CreateScope();
        KafkaIngressProcessor processor = scope.ServiceProvider.GetRequiredService<KafkaIngressProcessor>();

        await Should.ThrowAsync<InvalidOperationException>(() => processor.ProcessAsync(
            IngressRecords.Context(
                position,
                recipientId,
                body,
                KafkaIngressApi.ProducerHeaders(KafkaIngressFixture.SecondaryRequestedProducer)),
            CancellationToken.None));

        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .AnyAsync(notification => notification.IdempotencyKey == idempotencyKey)))
            .ShouldBeFalse();
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .AnyAsync(entry => entry.EntityId == $"{application}:{idempotencyKey}")))
            .ShouldBeFalse();
    }

    private async Task<string> AuditActorAsync(string action, string entityId)
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.Action == action && entry.EntityId == entityId)
            .Select(entry => entry.ActorId)
            .SingleAsync());

    private static async Task<KafkaDisposition> ProcessAsync(
        ServiceProvider provider,
        KafkaMessageContext context)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<KafkaIngressProcessor>()
            .ProcessAsync(context, CancellationToken.None);
    }

    private static void AssertPreTrustDeadLetter(
        ConsumeResult<string, byte[]> record,
        TopicPartitionOffset position,
        string reason)
    {
        IngressRecords.Header(record, DeadLetterHeaders.Reason).ShouldBe(reason);
        IngressRecords.Header(record, ProducerHeader).ShouldBe(KafkaIngressFixture.RequestedProducer);
        record.Message.Key.ShouldBe(KafkaIngressFixture.RequestedProducer);
        IsDeadLetterFor(record, position).ShouldBeTrue();
        IngressRecords.Header(record, DeadLetterHeaders.Redacted).ShouldBe("true");
        IngressRecords.Header(record, "application").ShouldBeNull();
        IngressRecords.Header(record, "class").ShouldBeNull();
        IngressRecords.Header(record, "idempotencyKey").ShouldBeNull();
        IngressRecords.Header(record, DeadLetterHeaders.Traceparent).ShouldBeNull();
    }

    private static bool IsDeadLetterFor(
        ConsumeResult<string, byte[]> record,
        TopicPartitionOffset position)
        => IngressRecords.Header(record, DeadLetterHeaders.SourceTopic) == position.Topic
            && IngressRecords.Header(record, DeadLetterHeaders.SourcePartition)
                == position.Partition.Value.ToString(CultureInfo.InvariantCulture)
            && IngressRecords.Header(record, DeadLetterHeaders.SourceOffset)
                == position.Offset.Value.ToString(CultureInfo.InvariantCulture);
}
