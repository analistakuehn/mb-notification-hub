using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

/// <summary>
/// The order of the ingress checks is the contract, not an implementation
/// detail. Each test here fails if a reordering silently leaks catalog
/// existence, inspects a payload the hub refuses to look at, or spends a
/// recipient's budget on a replay the producer is entitled to.
/// </summary>
[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class KafkaIngressCheckOrderTests(KafkaIngressFixture fixture)
{
    private const string Producer = "order-service";

    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    [RequiresDockerFact]
    public async Task An_unauthorized_producer_learns_nothing_about_which_templates_exist()
    {
        var application = KafkaIngressApi.NewApplication();
        (var publishedKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync(("granted-service", application, "transactional"));
        await using ServiceProvider provider = fixture.BuildIngressProvider();

        var existingKey = KafkaIngressApi.NewIdempotencyKey();
        var missingKey = KafkaIngressApi.NewIdempotencyKey();
        var recipientId = $"cus_{Guid.NewGuid():N}";

        KafkaDisposition askedExisting = await IngressRecords.ProcessAsync(
            fixture, provider, recipientId,
            KafkaIngressApi.RequestedEvent(
                application, publishedKey, "transactional", recipientId, existingKey),
            KafkaIngressApi.ProducerHeaders("intruder-service"));
        KafkaDisposition askedMissing = await IngressRecords.ProcessAsync(
            fixture, provider, recipientId,
            KafkaIngressApi.RequestedEvent(
                application, "template-that-was-never-published", "transactional", recipientId, missingKey),
            KafkaIngressApi.ProducerHeaders("intruder-service"));

        // Same answer for a template that exists and one that does not: the
        // registry decides before the catalog is ever consulted.
        askedExisting.ShouldBeOfType<KafkaDisposition.DeadLetter>().Reason.ShouldBe("producer-not-authorized");
        askedMissing.ShouldBeOfType<KafkaDisposition.DeadLetter>().Reason.ShouldBe("producer-not-authorized");

        // Falsification: an authorized principal does tell the two apart, so
        // the equality above is the authorization check and not a catalog that
        // answers the same way to everyone.
        var authorizedMissing = KafkaIngressApi.NewIdempotencyKey();
        KafkaDisposition authorized = await IngressRecords.ProcessAsync(
            fixture, provider, recipientId,
            KafkaIngressApi.RequestedEvent(
                application, "template-that-was-never-published", "transactional",
                recipientId, authorizedMissing),
            KafkaIngressApi.ProducerHeaders("granted-service"));
        authorized.ShouldBeOfType<KafkaDisposition.DeadLetter>().Reason.ShouldBe("template-not-found");
    }

    [RequiresDockerFact]
    public async Task A_sensitive_template_is_refused_before_anything_reports_on_the_payload()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) = await KafkaIngressApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", sensitiveVariables: ["code"]);
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var secret = $"otp-{Guid.NewGuid():N}";

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            // Variables that also fail the published schema: the wrong type
            // for the declared variable plus one the schema does not know.
            KafkaIngressApi.RequestedEvent(
                application, templateKey, "transactional", recipientId, idempotencyKey,
                variables: new { code = 12345, leftover = secret }),
            KafkaIngressApi.ProducerHeaders(Producer));

        // The schema report would describe the very payload the restriction
        // exists to keep unread, so the restriction answers first.
        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe("sensitive-variables-on-bus");

        ConsumeResult<string, byte[]> record = fixture
            .ReadAll(KafkaIngressFixture.DeadLetterTopic, ReadBudget)
            .Single(candidate => IngressRecords.Header(candidate, "idempotencyKey") == idempotencyKey);
        IngressRecords.Body(record).ShouldNotContain(secret);
    }

    [RequiresDockerFact]
    public async Task A_replay_does_not_spend_the_recipient_budget()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = KafkaIngressApi.RequestedEvent(
            application, templateKey, "transactional", recipientId, idempotencyKey);

        // A budget of exactly one request in the window: if the replay spent
        // it, the second answer would be the rate-limit refusal.
        await using ServiceProvider provider = fixture.BuildIngressProvider(new Dictionary<string, string?>
        {
            ["Modules:Notifications:RateLimits:PerRecipient:transactional:0:PermitLimit"] = "1",
            ["Modules:Notifications:RateLimits:PerRecipient:transactional:0:WindowSeconds"] = "120",
        });

        KafkaDisposition first = await IngressRecords.ProcessAsync(
            fixture, provider, recipientId, body, KafkaIngressApi.ProducerHeaders(Producer));
        KafkaDisposition replay = await IngressRecords.ProcessAsync(
            fixture, provider, recipientId, body, KafkaIngressApi.ProducerHeaders(Producer));

        first.ShouldBeOfType<KafkaDisposition.Processed>();
        replay.ShouldBeOfType<KafkaDisposition.Duplicate>();

        // Falsification of the budget itself: a different recipient request
        // beyond the window does get refused, so the replay above passed
        // because it never reached the counter.
        var otherKey = KafkaIngressApi.NewIdempotencyKey();
        KafkaDisposition beyond = await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            KafkaIngressApi.RequestedEvent(
                application, templateKey, "transactional", recipientId, otherKey),
            KafkaIngressApi.ProducerHeaders(Producer));
        beyond.ShouldBeOfType<KafkaDisposition.DeadLetter>().Reason.ShouldBe("recipient-rate-limited");

        // Exactly one notification exists for the replayed key.
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(notification => notification.Application == application
                && notification.IdempotencyKey == idempotencyKey)))
            .ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_malformed_payload_is_refused_for_its_shape_even_when_the_producer_is_unknown()
    {
        var application = KafkaIngressApi.NewApplication();
        await fixture.SeedProducerGrantsAsync(("someone-else", application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            // A class outside the vocabulary: the shape is wrong and the
            // producer is not authorized either, and shape answers first.
            KafkaIngressApi.RequestedEvent(
                application, "some-template", "banana", recipientId, idempotencyKey),
            KafkaIngressApi.ProducerHeaders("intruder-service"));

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>().Reason.ShouldBe("payload-invalid");

        var details = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.EntityId == $"{application}:{idempotencyKey}")
            .Select(entry => entry.DetailsJson)
            .SingleAsync());
        using JsonDocument audit = JsonDocument.Parse(details);
        audit.RootElement.GetProperty("reason").GetString().ShouldBe("payload-invalid");
    }
}
