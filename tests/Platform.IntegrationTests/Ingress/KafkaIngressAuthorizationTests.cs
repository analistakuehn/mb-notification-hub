using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class KafkaIngressAuthorizationTests(KafkaIngressFixture fixture)
{
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    [RequiresDockerFact]
    public async Task A_principal_outside_the_registry_is_refused_with_the_producer_reason()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync(("known-service", application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            KafkaIngressApi.RequestedEvent(
                application, templateKey, "transactional", recipientId, idempotencyKey),
            KafkaIngressApi.ProducerHeaders("stranger-service"));

        KafkaDisposition.DeadLetter refused = disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>();
        refused.Reason.ShouldBe("producer-not-authorized");

        ConsumeResult<string, byte[]> record = DeadLetterFor(idempotencyKey);
        IngressRecords.Header(record, "reason").ShouldBe("producer-not-authorized");
        IngressRecords.Header(record, "producer").ShouldBe("stranger-service");
        IngressRecords.Header(record, "sourceTopic").ShouldBe(KafkaIngressFixture.RequestedTopic);
        IngressRecords.Header(record, "sourceOffset").ShouldNotBeNull();
        IngressRecords.Header(record, "redacted").ShouldBe("false");

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
        await fixture.SeedProducerGrantsAsync(("billing-service", application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            KafkaIngressApi.RequestedEvent(application, templateKey, "critical", recipientId, idempotencyKey),
            KafkaIngressApi.ProducerHeaders("billing-service"));

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>().Reason.ShouldBe("producer-not-authorized");
        IngressRecords.Header(DeadLetterFor(idempotencyKey), "class").ShouldBe("critical");
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

    private ConsumeResult<string, byte[]> DeadLetterFor(string idempotencyKey)
        => fixture
            .ReadAll(KafkaIngressFixture.DeadLetterTopic, ReadBudget)
            .Single(record => IngressRecords.Header(record, "idempotencyKey") == idempotencyKey);

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
}
