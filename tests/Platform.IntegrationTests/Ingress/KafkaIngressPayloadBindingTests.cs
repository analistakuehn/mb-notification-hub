using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Features.Ingress;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class KafkaIngressPayloadBindingTests(KafkaIngressFixture fixture)
{
    private const string Producer = KafkaIngressFixture.RequestedProducer;

    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    [RequiresDockerTheory]
    [InlineData(3, true)]
    [InlineData(201, false)]
    public async Task An_invalid_idempotency_key_is_permanently_refused_before_notification_persistence(
        int length,
        bool whitespace)
    {
        var application = KafkaIngressApi.NewApplication();
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = new string(whitespace ? ' ' : 'k', length);
        var body = KafkaIngressApi.RequestedEvent(
            application,
            "template-is-never-read",
            "transactional",
            recipientId,
            idempotencyKey);

        await AssertPermanentPayloadRefusalAsync(application, recipientId, idempotencyKey, body);
    }

    [RequiresDockerTheory]
    [InlineData("scheduledAt", "\"not-a-timestamp\"")]
    [InlineData("locale", "42")]
    [InlineData("variables", "[]")]
    [InlineData("metadata", "\"not-an-object\"")]
    [InlineData("channelsHint", "\"push\"")]
    [InlineData("correlationId", "{}")]
    [InlineData("channelsHint", "[\"push\", 7]")]
    public async Task A_malformed_optional_field_is_permanently_refused_as_payload_invalid(
        string field,
        string invalidJson)
    {
        var application = KafkaIngressApi.NewApplication();
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var validBody = KafkaIngressApi.RequestedEvent(
            application,
            "template-is-never-read",
            "transactional",
            recipientId,
            idempotencyKey);
        var malformedBody = WithDataField(validBody, field, invalidJson);

        await AssertPermanentPayloadRefusalAsync(
            application,
            recipientId,
            idempotencyKey,
            malformedBody);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Optional_fields_that_are_absent_or_null_bind_as_absent(bool explicitNull)
    {
        JsonObject data = ValidData("key-binding");
        if (explicitNull)
        {
            data["locale"] = null;
            data["variables"] = null;
            data["metadata"] = null;
            data["channelsHint"] = null;
            data["correlationId"] = null;
            data["scheduledAt"] = null;
        }

        using JsonDocument document = JsonDocument.Parse(data.ToJsonString());

        IngressRequest? request = IngressRequestBinder.Bind(document.RootElement);

        request.ShouldNotBeNull();
        request.Command.Locale.ShouldBeNull();
        request.Command.Variables.ShouldBeNull();
        request.Command.Metadata.ShouldBeNull();
        request.Command.ChannelsHint.ShouldBeNull();
        request.Command.CorrelationId.ShouldBeNull();
        request.Command.ScheduledAt.ShouldBeNull();
    }

    [Fact]
    public void An_idempotency_key_at_the_maximum_length_binds()
    {
        var idempotencyKey = new string('k', 200);
        JsonObject data = ValidData(idempotencyKey);
        using JsonDocument document = JsonDocument.Parse(data.ToJsonString());

        IngressRequest? request = IngressRequestBinder.Bind(document.RootElement);

        request.ShouldNotBeNull();
        request.IdempotencyKey.ShouldBe(idempotencyKey);
    }

    private async Task AssertPermanentPayloadRefusalAsync(
        string application,
        string recipientId,
        string idempotencyKey,
        string body)
    {
        Dictionary<string, string> headers = KafkaIngressApi.ProducerHeaders(Producer);
        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic,
            recipientId,
            body,
            headers);
        KafkaMessageContext context = IngressRecords.Context(position, recipientId, body, headers);
        await using ServiceProvider provider = fixture.BuildIngressProvider();
        using IServiceScope scope = provider.CreateScope();

        KafkaDisposition disposition = await scope.ServiceProvider
            .GetRequiredService<KafkaIngressProcessor>()
            .ProcessAsync(context, CancellationToken.None);

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(NotificationRejectionReasons.PayloadInvalid);
        ConsumeResult<string, byte[]> deadLetter = fixture
            .ReadAll(KafkaIngressFixture.DeadLetterTopic, ReadBudget)
            .Single(record => IsDeadLetterFor(record, position));
        IngressRecords.Header(deadLetter, DeadLetterHeaders.Reason)
            .ShouldBe(NotificationRejectionReasons.PayloadInvalid);
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .AnyAsync(notification => notification.Application == application)))
            .ShouldBeFalse();
        (await fixture.QueryNotificationsDbAsync(db => db.IdempotencyRegistrations
            .AsNoTracking()
            .AnyAsync(registration => registration.Application == application
                && registration.IdempotencyKey == idempotencyKey)))
            .ShouldBeFalse();
        (await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(mark => mark.MessageId == context.DedupeId)))
            .ShouldBeTrue();
    }

    private static bool IsDeadLetterFor(
        ConsumeResult<string, byte[]> record,
        TopicPartitionOffset position)
        => IngressRecords.Header(record, DeadLetterHeaders.SourceTopic) == position.Topic
            && IngressRecords.Header(record, DeadLetterHeaders.SourcePartition)
                == position.Partition.Value.ToString(CultureInfo.InvariantCulture)
            && IngressRecords.Header(record, DeadLetterHeaders.SourceOffset)
                == position.Offset.Value.ToString(CultureInfo.InvariantCulture);

    private static JsonObject ValidData(string idempotencyKey)
        => new()
        {
            ["application"] = "app-binding",
            ["recipientId"] = "cus_binding",
            ["idempotencyKey"] = idempotencyKey,
            ["class"] = "transactional",
            ["templateKey"] = "template-binding",
            ["ttlSeconds"] = 300,
        };

    private static string WithDataField(string body, string field, string json)
    {
        JsonObject envelope = JsonNode.Parse(body)?.AsObject()
            ?? throw new InvalidOperationException("O evento de teste deve ser um objeto JSON.");
        JsonObject data = envelope["data"]?.AsObject()
            ?? throw new InvalidOperationException("O evento de teste deve conter data.");
        data[field] = JsonNode.Parse(json);
        return envelope.ToJsonString();
    }
}
