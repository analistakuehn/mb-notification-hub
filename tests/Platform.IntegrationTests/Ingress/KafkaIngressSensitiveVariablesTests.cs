using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

/// <summary>
/// The bus restriction and its own mitigation. Refusing a request that carries
/// a secret is worth nothing if the refusal copies the secret onto a topic
/// that keeps it fourteen times longer, so both halves are measured here.
/// </summary>
[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class KafkaIngressSensitiveVariablesTests(KafkaIngressFixture fixture)
{
    private const string Producer = KafkaIngressFixture.RequestedProducer;

    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    [RequiresDockerFact]
    public async Task A_template_that_declares_sensitive_variables_is_refused_even_without_them_in_the_payload()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) = await KafkaIngressApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", sensitiveVariables: ["code"]);
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            // The payload carries the declared variable with a harmless value
            // and would pass the schema; the rule depends on the declaration
            // alone, so the producer can decide before publishing.
            KafkaIngressApi.RequestedEvent(
                application, templateKey, "transactional", recipientId, idempotencyKey,
                new KafkaIngressApi.RequestedEventOptions
                {
                    Variables = new { code = "483920" },
                }),
            KafkaIngressApi.ProducerHeaders(Producer));

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe("sensitive-variables-on-bus");
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .AnyAsync(notification => notification.IdempotencyKey == idempotencyKey)))
            .ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task The_dead_letter_record_carries_the_declared_names_and_never_a_value()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) = await KafkaIngressApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", sensitiveVariables: ["code"]);
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var secret = $"otp-{Guid.NewGuid():N}";

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            KafkaIngressApi.RequestedEvent(
                application, templateKey, "transactional", recipientId, idempotencyKey,
                new KafkaIngressApi.RequestedEventOptions
                {
                    Variables = new { code = secret },
                }),
            KafkaIngressApi.ProducerHeaders(Producer));

        ConsumeResult<string, byte[]> record = fixture
            .ReadAll(KafkaIngressFixture.DeadLetterTopic, ReadBudget)
            .Single(candidate => IngressRecords.Header(candidate, "idempotencyKey") == idempotencyKey);

        var body = IngressRecords.Body(record);
        body.ShouldNotContain(secret);
        IngressRecords.Header(record, "redacted").ShouldBe("true");

        using JsonDocument published = JsonDocument.Parse(body);
        JsonElement variables = published.RootElement.GetProperty("data").GetProperty("variables");
        variables.ValueKind.ShouldBe(JsonValueKind.Array);
        variables.EnumerateArray().Select(item => item.GetString()).ShouldBe(["code"]);

        // The diagnostics the producing team needs survive the redaction.
        published.RootElement.GetProperty("data").GetProperty("templateKey").GetString()
            .ShouldBe(templateKey);
    }

    [RequiresDockerFact]
    public async Task A_template_without_sensitive_variables_keeps_its_original_body_on_the_dead_letter_topic()
    {
        // Falsification of the redaction: it must fire for one reason only.
        // A refusal for any other reason preserves the body, which is what
        // makes an audited redrive possible at all.
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var marker = $"mark-{Guid.NewGuid():N}";

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            KafkaIngressApi.RequestedEvent(
                application, "template-that-was-never-published", "transactional",
                recipientId, idempotencyKey,
                new KafkaIngressApi.RequestedEventOptions
                {
                    Variables = new { code = marker },
                }),
            KafkaIngressApi.ProducerHeaders(Producer));

        ConsumeResult<string, byte[]> record = fixture
            .ReadAll(KafkaIngressFixture.DeadLetterTopic, ReadBudget)
            .Single(candidate => IngressRecords.Header(candidate, "idempotencyKey") == idempotencyKey);

        IngressRecords.Header(record, "reason").ShouldBe("template-not-found");
        IngressRecords.Header(record, "redacted").ShouldBe("false");
        IngressRecords.Body(record).ShouldContain(marker);
        templateKey.ShouldNotBeNullOrEmpty();
    }
}
