using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Features.Ingress;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class KafkaIngressAuthorizationTests(KafkaIngressFixture fixture)
{
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    [RequiresDockerFact]
    public async Task A_principal_outside_the_registry_is_refused_with_the_producer_reason()
    {
        const string secret = "producer-not-authorized-secret-must-not-reach-dlt";
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync(("known-service", application, "transactional"));
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
                Variables = new { apiToken = secret },
            });

        Dictionary<string, string> headers = KafkaIngressApi.ProducerHeaders("stranger-service");
        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic,
            recipientId,
            body,
            headers);
        KafkaMessageContext context = IngressRecords.Context(position, recipientId, body, headers);
        await using ServiceProvider provider = fixture.BuildIngressProvider();

        KafkaDisposition disposition = await ProcessAsync(provider, context);

        KafkaDisposition.DeadLetter refused = disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>();
        refused.Reason.ShouldBe("producer-not-authorized");

        ConsumeResult<string, byte[]> record = DeadLetterFor(position);
        AssertPreTrustDeadLetter(record, position, "producer-not-authorized");
        AssertSecretAbsent(record, secret);

        // Nothing was accepted, and the refusal left its own trail.
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .AnyAsync(notification => notification.IdempotencyKey == idempotencyKey)))
            .ShouldBeFalse();
        (await RejectionAuditReasonAsync(application, idempotencyKey)).ShouldBe("producer-not-authorized");
    }

    [RequiresDockerFact]
    public async Task A_principal_asking_a_class_it_was_not_granted_is_refused_the_same_way()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "critical");
        // Granted for another class of the same application on purpose: the
        // grant is the triple, never the principal alone.
        await fixture.SeedProducerGrantsAsync(
            (KafkaIngressFixture.RequestedProducer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();

        var body = KafkaIngressApi.RequestedEvent(
            application,
            templateKey,
            "critical",
            recipientId,
            idempotencyKey);
        Dictionary<string, string> headers = KafkaIngressApi.ProducerHeaders("billing-service");
        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic,
            recipientId,
            body,
            headers);
        KafkaMessageContext context = IngressRecords.Context(position, recipientId, body, headers);
        await using ServiceProvider provider = fixture.BuildIngressProvider();

        KafkaDisposition disposition = await ProcessAsync(provider, context);

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>().Reason.ShouldBe("producer-not-authorized");
        AssertPreTrustDeadLetter(DeadLetterFor(position), position, "producer-not-authorized");
    }

    [RequiresDockerFact]
    public async Task An_empty_registry_keeps_the_role_from_consuming_and_reports_it_unhealthy()
    {
        await fixture.ClearProducerGrantsAsync();
        await using ServiceProvider provider = fixture.BuildIngressProvider();

        try
        {
            IKafkaConsumerGate gate = provider.GetRequiredService<IKafkaConsumerGate>();
            KafkaGateDecision decision = await gate.EvaluateAsync(CancellationToken.None);

            // An empty table is indistinguishable from a materialization that
            // never ran; consuming would send a day of legitimate traffic to
            // the dead-letter topic while every probe reported success.
            decision.CanConsume.ShouldBeFalse();
            decision.Reason.ShouldNotBeNull();

            HealthReport report = await provider
                .GetRequiredService<HealthCheckService>()
                .CheckHealthAsync();
            report.Entries["kafka-consumer-gate"].Status.ShouldBe(HealthStatus.Unhealthy);
        }
        finally
        {
            // Falsification of the same gate: with a grant present it opens,
            // so the assertions above measure the registry and not a gate that
            // is closed no matter what.
            await fixture.SeedProducerGrantsAsync(("gate-probe", "gate-probe-app", "transactional"));
        }

        await using ServiceProvider reopened = fixture.BuildIngressProvider();
        (await reopened.GetRequiredService<IKafkaConsumerGate>()
            .EvaluateAsync(CancellationToken.None))
            .CanConsume.ShouldBeTrue();
    }

    private ConsumeResult<string, byte[]> DeadLetterFor(TopicPartitionOffset position)
        => fixture
            .ReadAll(KafkaIngressFixture.DeadLetterTopic, ReadBudget)
            .Single(record => IsDeadLetterFor(record, position));

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

    private async Task<string?> RejectionAuditReasonAsync(string application, string idempotencyKey)
    {
        var details = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.Action == "notification.rejected_at_ingress"
                && entry.EntityId == $"{application}:{idempotencyKey}")
            .Select(entry => entry.DetailsJson)
            .SingleAsync());
        using JsonDocument audit = JsonDocument.Parse(details);
        audit.RootElement.GetProperty("source").GetString().ShouldBe("kafka");
        return audit.RootElement.GetProperty("reason").GetString();
    }

    private static void AssertSecretAbsent(ConsumeResult<string, byte[]> record, string secret)
    {
        Encoding.UTF8.GetString(record.Message.Value ?? []).ShouldNotContain(secret);
        IngressRecords.Body(record).ShouldNotContain(secret);
        record.Message.Headers.ShouldAllBe(header =>
            !header.Key.Contains(secret, StringComparison.Ordinal)
            && !Encoding.UTF8.GetString(header.GetValueBytes()).Contains(secret, StringComparison.Ordinal));
    }
}
