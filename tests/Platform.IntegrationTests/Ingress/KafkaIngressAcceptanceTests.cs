using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class KafkaIngressAcceptanceTests(KafkaIngressFixture fixture)
{
    private const string Producer = KafkaIngressFixture.RequestedProducer;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    [RequiresDockerFact]
    public async Task A_valid_event_is_accepted_with_the_same_rows_the_synchronous_route_writes()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, var templateVersion) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();

        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic,
            recipientId,
            KafkaIngressApi.RequestedEvent(
                application, templateKey, "transactional", recipientId, idempotencyKey),
            KafkaIngressApi.ProducerHeaders(Producer));

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        Notification notification = await RunUntilSettledAsync(provider, application, idempotencyKey, position);

        // The same four writes the synchronous route commits: the notification
        // row, its idempotency registration, the outbox message to the core
        // queue of the class, and the audit trail entry.
        notification.Status.ShouldBe(NotificationStatuses.Accepted);
        notification.TemplateKey.ShouldBe(templateKey);
        notification.TemplateVersion.ShouldBe(templateVersion);
        notification.RequestedBy.ShouldBe(Producer);
        notification.VariablesEncrypted.ShouldNotBeNull();

        IdempotencyRegistration registration = await fixture.QueryNotificationsDbAsync(db =>
            db.IdempotencyRegistrations
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Application == application
                    && candidate.IdempotencyKey == idempotencyKey));
        registration.NotificationId.ShouldBe(notification.Id);

        OutboxMessage queued = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.MessageKey == recipientId
                && message.Destination == "core-transactional"));
        queued.Transport.ShouldBe(OutboxTransports.Sqs);
        queued.EventType.ShouldBe("notification.accepted");

        // The trail records the origin: the same action as the REST route,
        // with the source that says which transport carried the request.
        var details = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.Action == "notification.accepted"
                && entry.EntityId == notification.Id.ToString())
            .Select(entry => entry.DetailsJson)
            .SingleAsync());
        using var audit = JsonDocument.Parse(details);
        audit.RootElement.GetProperty("source").GetString().ShouldBe("kafka");
        audit.RootElement.GetProperty("idempotencyKey").GetString().ShouldBe(idempotencyKey);

        // At-least-once rests on the marks, and the offset advanced past the
        // record the consumer settled. The wait above only knows the record was
        // marked; the consumer name is what says which role owns the mark.
        var markedBy = await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
            .AsNoTracking()
            .Where(mark => mark.MessageId
                == $"{position.Topic}:{position.Partition.Value}:{position.Offset.Value}")
            .Select(mark => mark.Consumer)
            .SingleAsync());
        markedBy.ShouldBe("kafka-ingress");

        var committed = fixture.CommittedOffset(position.Topic, position.Partition.Value);
        committed.ShouldNotBeNull();
        committed.Value.ShouldBeGreaterThan(position.Offset.Value);
    }

    /// <summary>
    /// Starts the hosted consumer of the role, waits until the record is fully
    /// settled, and stops it. Hosting the real service is the point:
    /// subscription, gate, per-record settling and offset commit are what this
    /// criterion is about.
    ///
    /// Settled means the effect and its deduplication mark, not the effect
    /// alone. This consumer commits the mark in a short transaction right after
    /// the effect, on purpose, so stopping the host the instant the
    /// notification row appears would cancel the settlement halfway and read a
    /// state no deployment ever leaves behind.
    /// </summary>
    private async Task<Notification> RunUntilSettledAsync(
        ServiceProvider provider,
        string application,
        string idempotencyKey,
        TopicPartitionOffset position)
    {
        IHostedService[] hosted = [.. provider.GetServices<IHostedService>()];
        using var stopping = new CancellationTokenSource(Budget);
        foreach (IHostedService service in hosted)
        {
            await service.StartAsync(stopping.Token);
        }

        try
        {
            var dedupeId = $"{position.Topic}:{position.Partition.Value}:{position.Offset.Value}";
            DateTimeOffset deadline = DateTimeOffset.UtcNow + Budget;
            while (DateTimeOffset.UtcNow < deadline)
            {
                Notification? candidate = await fixture.QueryNotificationsDbAsync(db => db.Notifications
                    .AsNoTracking()
                    .SingleOrDefaultAsync(notification => notification.Application == application
                        && notification.IdempotencyKey == idempotencyKey));
                var settled = candidate is not null
                    && await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
                        .AsNoTracking()
                        .AnyAsync(mark => mark.MessageId == dedupeId));
                if (settled)
                {
                    return candidate!;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            throw new TimeoutException(
                "O ingress não liquidou o evento dentro do orçamento do teste.");
        }
        finally
        {
            foreach (IHostedService service in hosted)
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
    }
}
