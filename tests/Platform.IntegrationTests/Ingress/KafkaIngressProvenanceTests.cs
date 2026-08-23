using System.Net;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

/// <summary>
/// Provenance of a bus request in the audit trail. The trail is append-only
/// and hash-chained, so a row written without the coordinates of its source
/// record is never corrected, only supplemented: they belong to the first
/// write or they are lost for that request. They are also what turns "the
/// producer denies asking" into a claim anyone can check against the record
/// the broker still holds.
/// </summary>
[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class KafkaIngressProvenanceTests(KafkaIngressFixture fixture)
{
    private const string Producer = "provenance-service";

    [RequiresDockerFact]
    public async Task An_accepted_bus_event_records_its_source_coordinates_on_the_trail()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = KafkaIngressApi.RequestedEvent(
            application, templateKey, "transactional", recipientId, idempotencyKey);
        var eventId = EnvelopeIdOf(body);

        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic, recipientId, body,
            KafkaIngressApi.ProducerHeaders(Producer));
        await using ServiceProvider provider = fixture.BuildIngressProvider();
        using (IServiceScope scope = provider.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<Api.Modules.Notifications.Features.Ingress.KafkaIngressProcessor>()
                .ProcessAsync(
                    IngressRecords.Context(
                        position, recipientId, body, KafkaIngressApi.ProducerHeaders(Producer)),
                    CancellationToken.None);
        }

        Guid notificationId = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Application == application
                && notification.IdempotencyKey == idempotencyKey)
            .Select(notification => notification.Id)
            .SingleAsync());

        (var actorId, JsonElement details) = await TrailEntryAsync(
            "notification.accepted", notificationId.ToString());

        // The principal is the actor of the entry; the coordinates say which
        // record on which partition carried its request.
        actorId.ShouldBe(Producer);
        AssertCoordinates(details, position, eventId);
    }

    [RequiresDockerFact]
    public async Task A_refused_bus_event_records_the_same_source_coordinates_on_the_trail()
    {
        var application = KafkaIngressApi.NewApplication();
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = KafkaIngressApi.RequestedEvent(
            application, "template-that-was-never-published", "transactional", recipientId, idempotencyKey);
        var eventId = EnvelopeIdOf(body);

        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic, recipientId, body,
            KafkaIngressApi.ProducerHeaders(Producer));
        await using ServiceProvider provider = fixture.BuildIngressProvider();
        using (IServiceScope scope = provider.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<Api.Modules.Notifications.Features.Ingress.KafkaIngressProcessor>()
                .ProcessAsync(
                    IngressRecords.Context(
                        position, recipientId, body, KafkaIngressApi.ProducerHeaders(Producer)),
                    CancellationToken.None);
        }

        (var actorId, JsonElement details) = await TrailEntryAsync(
            "notification.rejected_at_ingress", $"{application}:{idempotencyKey}");

        actorId.ShouldBe(Producer);
        details.GetProperty("reason").GetString().ShouldBe("template-not-found");
        AssertCoordinates(details, position, eventId);
    }

    [RequiresDockerFact]
    public async Task A_synchronous_request_records_no_source_coordinates()
    {
        // Falsification: the coordinates are the bus provenance, not a field
        // stamped on everything. A synchronous call has no record to point at,
        // and writing empty coordinates would be worse than writing none.
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        var recipientId = $"cus_{Guid.NewGuid():N}";

        HttpClient rest = fixture.CreateProducerClient(
            "rest-producer", NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            rest,
            new
            {
                application,
                recipientId,
                @class = "transactional",
                templateKey,
                locale = "pt-BR",
                variables = new { code = "123456" },
                ttlSeconds = 300,
            },
            Guid.NewGuid().ToString("N"));
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        Guid notificationId = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Application == application)
            .Select(notification => notification.Id)
            .SingleAsync());

        (_, JsonElement details) = await TrailEntryAsync(
            "notification.accepted", notificationId.ToString());

        details.GetProperty("source").GetString().ShouldBe("rest");
        details.GetProperty("origin").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static void AssertCoordinates(
        JsonElement details,
        TopicPartitionOffset position,
        string eventId)
    {
        details.GetProperty("source").GetString().ShouldBe("kafka");
        JsonElement origin = details.GetProperty("origin");
        origin.GetProperty("topic").GetString().ShouldBe(position.Topic);
        origin.GetProperty("partition").GetInt32().ShouldBe(position.Partition.Value);
        origin.GetProperty("offset").GetInt64().ShouldBe(position.Offset.Value);
        origin.GetProperty("eventId").GetString().ShouldBe(eventId);
    }

    private async Task<(string ActorId, JsonElement Details)> TrailEntryAsync(string action, string entityId)
    {
        // Projected into an anonymous type on purpose: the provider has no
        // reader for a tuple, so a tuple projection fails at materialization.
        var entry = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(candidate => candidate.Action == action && candidate.EntityId == entityId)
            .Select(candidate => new { candidate.ActorId, candidate.DetailsJson })
            .SingleAsync());
        using JsonDocument document = JsonDocument.Parse(entry.DetailsJson);
        return (entry.ActorId, document.RootElement.Clone());
    }

    private static string EnvelopeIdOf(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
