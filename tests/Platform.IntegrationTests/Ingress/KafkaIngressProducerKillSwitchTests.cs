using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Ingress;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class KafkaIngressProducerKillSwitchTests(KafkaIngressFixture fixture)
{
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    [RequiresDockerFact]
    public async Task A_blocked_topic_producer_is_refused_before_the_registry_despite_claims_for_another_producer()
    {
        const string secret = "producer-disabled-secret-must-not-reach-dlt";
        var application = KafkaIngressApi.NewApplication();
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = KafkaIngressApi.RequestedEvent(
            application,
            "template-is-never-read",
            "transactional",
            recipientId,
            idempotencyKey,
            new KafkaIngressApi.RequestedEventOptions
            {
                Variables = new { apiToken = secret },
                EventSource = $"urn:araia:{KafkaIngressFixture.SecondaryRequestedProducer}",
            });
        Dictionary<string, string> headers =
            KafkaIngressApi.ProducerHeaders(KafkaIngressFixture.SecondaryRequestedProducer);
        var killSwitch = new RecordingKillSwitch(KillSwitchEvaluation.Blocked);
        await using ServiceProvider provider = fixture.BuildIngressProvider(
            replaceServices: services => ReplaceAuthorities(services, killSwitch));
        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic,
            recipientId,
            body,
            headers);
        KafkaMessageContext context = IngressRecords.Context(position, recipientId, body, headers);

        KafkaDisposition disposition = await ProcessAsync(provider, context);

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe("producer-disabled");
        killSwitch.Evaluations.ShouldBe(
            [(KillSwitchScope.Producer, KafkaIngressFixture.RequestedProducer)]);
        ConsumeResult<string, byte[]> deadLetter = fixture
            .ReadAll(KafkaIngressFixture.DeadLetterTopic, ReadBudget)
            .Single(record => IsDeadLetterFor(record, position));
        AssertPreTrustDeadLetter(deadLetter, position, "producer-disabled");
        AssertSecretAbsent(deadLetter, secret);
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .AnyAsync(notification => notification.IdempotencyKey == idempotencyKey)))
            .ShouldBeFalse();
        (await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(mark => mark.MessageId == context.DedupeId)))
            .ShouldBeTrue();

        var trail = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.Action == "notification.rejected_at_ingress"
                && entry.EntityId == $"{application}:{idempotencyKey}")
            .Select(entry => new { entry.ActorId, entry.DetailsJson })
            .SingleAsync());
        trail.ActorId.ShouldBe(KafkaIngressFixture.RequestedProducer);
        using JsonDocument details = JsonDocument.Parse(trail.DetailsJson);
        details.RootElement.GetProperty("reason").GetString().ShouldBe("producer-disabled");
    }

    [RequiresDockerFact]
    public async Task An_unavailable_switch_retries_without_dead_letter_trail_or_deduplication_mark()
    {
        var application = KafkaIngressApi.NewApplication();
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = KafkaIngressApi.RequestedEvent(
            application,
            "template-is-never-read",
            "transactional",
            recipientId,
            idempotencyKey,
            new KafkaIngressApi.RequestedEventOptions
            {
                EventSource = $"urn:araia:{KafkaIngressFixture.SecondaryRequestedProducer}",
            });
        Dictionary<string, string> headers =
            KafkaIngressApi.ProducerHeaders(KafkaIngressFixture.SecondaryRequestedProducer);
        var killSwitch = new RecordingKillSwitch(KillSwitchEvaluation.Unavailable);
        await using ServiceProvider provider = fixture.BuildIngressProvider(
            replaceServices: services => ReplaceAuthorities(services, killSwitch));
        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic,
            recipientId,
            body,
            headers);
        KafkaMessageContext context = IngressRecords.Context(position, recipientId, body, headers);

        KafkaDisposition disposition = await ProcessAsync(provider, context);

        disposition.ShouldBeOfType<KafkaDisposition.Retry>();
        killSwitch.Evaluations.ShouldBe(
            [(KillSwitchScope.Producer, KafkaIngressFixture.RequestedProducer)]);
        fixture.ReadAll(KafkaIngressFixture.DeadLetterTopic, ReadBudget)
            .ShouldNotContain(record => IsDeadLetterFor(record, position));
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .AnyAsync(entry => entry.EntityId == $"{application}:{idempotencyKey}")))
            .ShouldBeFalse();
        (await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(mark => mark.MessageId == context.DedupeId)))
            .ShouldBeFalse();
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .AnyAsync(notification => notification.IdempotencyKey == idempotencyKey)))
            .ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task An_invalid_body_is_refused_before_the_producer_switch_and_registry()
    {
        var application = KafkaIngressApi.NewApplication();
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = KafkaIngressApi.RequestedEvent(
            application,
            "template-is-never-read",
            "banana",
            recipientId,
            idempotencyKey);
        var killSwitch = new RecordingKillSwitch(KillSwitchEvaluation.Blocked);
        await using ServiceProvider provider = fixture.BuildIngressProvider(
            replaceServices: services => ReplaceAuthorities(services, killSwitch));

        KafkaDisposition disposition = await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            body,
            KafkaIngressApi.ProducerHeaders(KafkaIngressFixture.SecondaryRequestedProducer));

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe("payload-invalid");
        killSwitch.Evaluations.ShouldBeEmpty();
    }

    private static void ReplaceAuthorities(
        IServiceCollection services,
        RecordingKillSwitch killSwitch)
    {
        services.RemoveAll<IKillSwitch>();
        services.AddSingleton<IKillSwitch>(killSwitch);
        services.RemoveAll<IProducerRegistry>();
        services.AddSingleton<IProducerRegistry, ThrowingProducerRegistry>();
    }

    private static async Task<KafkaDisposition> ProcessAsync(
        ServiceProvider provider,
        KafkaMessageContext context)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<KafkaIngressProcessor>()
            .ProcessAsync(context, CancellationToken.None);
    }

    private static void AssertSecretAbsent(ConsumeResult<string, byte[]> record, string secret)
    {
        Encoding.UTF8.GetString(record.Message.Value ?? []).ShouldNotContain(secret);
        IngressRecords.Body(record).ShouldNotContain(secret);
        record.Message.Headers.ShouldAllBe(header =>
            !header.Key.Contains(secret, StringComparison.Ordinal)
            && !Encoding.UTF8.GetString(header.GetValueBytes()).Contains(secret, StringComparison.Ordinal));
    }

    private static void AssertPreTrustDeadLetter(
        ConsumeResult<string, byte[]> record,
        TopicPartitionOffset position,
        string reason)
    {
        IngressRecords.Header(record, DeadLetterHeaders.Reason).ShouldBe(reason);
        IngressRecords.Header(record, "producer").ShouldBe(KafkaIngressFixture.RequestedProducer);
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

    private sealed class RecordingKillSwitch(KillSwitchEvaluation result) : IKillSwitch
    {
        public List<(KillSwitchScope Scope, string Key)> Evaluations { get; } = [];

        public ValueTask<KillSwitchEvaluation> EvaluateAsync(
            KillSwitchScope scope,
            string key,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Evaluations.Add((scope, key));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingProducerRegistry : IProducerRegistry
    {
        public Task<ProducerGrants?> CurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromException<ProducerGrants?>(
                new InvalidOperationException("O registro não deve ser consultado antes do kill switch."));
        }
    }
}
