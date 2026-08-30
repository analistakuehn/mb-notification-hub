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

    /// <summary>
    /// Raw text on purpose. Spelled as a C# escape the compiler would fold it
    /// into one code unit and the record under test would never carry the six
    /// characters that make the payload unreadable.
    /// </summary>
    private const string LoneSurrogateEscape = @"\ud800";

    /// <summary>
    /// Stands in for the escape while the body is still being built as a node
    /// tree, and is replaced by raw text afterwards. A node tree rewrites what
    /// it serializes, so an escape put in before that point leaves as a
    /// replacement character and the record under test carries no fault at all.
    /// </summary>
    private const string EscapePlaceholder = "SURROGATE-ESCAPE-GOES-HERE";

    /// <summary>
    /// Seven escapes. A property lookup only unescapes a candidate key whose
    /// escaped length reaches the length of the name being sought, so one
    /// escape breaks the short names and leaves the long ones working. Seven
    /// is past every field name the binder looks up, which is what makes the
    /// refusal independent of which name happens to be read first.
    /// </summary>
    private const string PoisonedKeyEscape =
        @"\ud800\ud800\ud800\ud800\ud800\ud800\ud800";

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

    /// <summary>
    /// The grave one. A payload whose escape names no character parses without
    /// complaint, so the binder accepts it and only the transcoding that every
    /// later step performs discovers it. Thrown from the processor it is
    /// indistinguishable from a transient failure: the consumer retries in
    /// process, then pauses the partition without advancing the offset, and
    /// the same record comes back and throws again. What must happen instead
    /// is the disposition every permanently unprocessable payload already
    /// gets, so one record dies and the partition keeps moving.
    /// </summary>
    [RequiresDockerTheory]
    [InlineData("variables")]
    [InlineData("metadata")]
    public async Task A_payload_whose_escape_names_no_character_is_permanently_refused_as_payload_invalid(
        string field)
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
        var withPlaceholder = WithDataField(
            validBody, field, $$"""{"probe":"{{EscapePlaceholder}}"}""");
        var unreadableBody = withPlaceholder.Replace(
            EscapePlaceholder, LoneSurrogateEscape, StringComparison.Ordinal);

        // The premise, asserted rather than assumed: the escape survived into
        // the bytes that reach the broker. Without this the test would settle a
        // record that never carried the fault and would pass either way.
        unreadableBody.Contains(LoneSurrogateEscape, StringComparison.Ordinal)
            .ShouldBeTrue("O corpo publicado deve carregar o escape cru.");

        await AssertPermanentPayloadRefusalAsync(
            application,
            recipientId,
            idempotencyKey,
            unreadableBody);
    }

    /// <summary>
    /// The same fault one step earlier than the validator. A key of the event
    /// body whose escape names no character is discovered by the property
    /// lookup itself, which unescapes candidate keys to compare them, so the
    /// binder throws before any rule of the ingestion runs. Thrown there it is
    /// indistinguishable from a transient failure and stops the partition, so
    /// it has to take the refusal the binder already has for a malformed body.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_body_key_that_names_no_character_is_permanently_refused_as_payload_invalid()
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

        // The key is spliced as raw text through a placeholder, because a node
        // tree rewrites what it serializes and an escape put in before that
        // point leaves as a replacement character.
        var withPlaceholder = WithDataKey(validBody, EscapePlaceholder);
        var unreadableBody = withPlaceholder.Replace(
            EscapePlaceholder, PoisonedKeyEscape, StringComparison.Ordinal);

        // The premise, asserted rather than assumed: the escape survived into
        // the bytes that reach the broker. Without this the test would settle a
        // record that never carried the fault and would pass either way.
        unreadableBody.Contains(PoisonedKeyEscape, StringComparison.Ordinal)
            .ShouldBeTrue("O corpo publicado deve carregar a chave com o escape cru.");

        await AssertPermanentPayloadRefusalAsync(
            application,
            recipientId,
            idempotencyKey,
            unreadableBody);
    }

    /// <summary>
    /// The falsifying half of the two refusals above. A body whose extra
    /// top-level key is ordinary still binds and still travels the accepting
    /// path, so the refusal is aimed at what cannot be read and not at every
    /// record that carries a key this contract does not name.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_body_with_an_ordinary_extra_key_still_binds_and_is_accepted()
    {
        var application = KafkaIngressApi.NewApplication();
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        (var templateKey, _) = await KafkaIngressApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var body = WithDataKey(
            KafkaIngressApi.RequestedEvent(
                application, templateKey, "transactional", recipientId, idempotencyKey),
            "unknownFutureField");

        Dictionary<string, string> headers = KafkaIngressApi.ProducerHeaders(Producer);
        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic, recipientId, body, headers);
        await using ServiceProvider provider = fixture.BuildIngressProvider();
        using IServiceScope scope = provider.CreateScope();

        KafkaDisposition disposition = await scope.ServiceProvider
            .GetRequiredService<KafkaIngressProcessor>()
            .ProcessAsync(
                IngressRecords.Context(position, recipientId, body, headers),
                CancellationToken.None);

        disposition.ShouldBeOfType<KafkaDisposition.Processed>();
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .AnyAsync(notification => notification.Application == application)))
            .ShouldBeTrue();
    }

    /// <summary>
    /// The validator still owns the payload rules on this transport, and the
    /// binder guard did not take them over. An oversized payload binds cleanly
    /// and dies at the validator, which is the chain the bus refusal rests on:
    /// invalid validation, then the handler's payload-invalid outcome, then the
    /// dead-letter record.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_oversized_payload_binds_and_is_refused_by_the_validator()
    {
        var application = KafkaIngressApi.NewApplication();
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = WithDataField(
            KafkaIngressApi.RequestedEvent(
                application,
                "template-is-never-read",
                "transactional",
                recipientId,
                idempotencyKey),
            "variables",
            $$"""{"blob":"{{new string('x', 300_000)}}"}""");

        await AssertPermanentPayloadRefusalAsync(
            application,
            recipientId,
            idempotencyKey,
            body);
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

        using var document = JsonDocument.Parse(data.ToJsonString());

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
        using var document = JsonDocument.Parse(data.ToJsonString());

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

    /// <summary>
    /// Adds one key to the event body, with an inert value. The name travels
    /// through the node tree unchanged, which is what lets a placeholder be
    /// swapped for raw text afterwards.
    /// </summary>
    private static string WithDataKey(string body, string key)
        => WithDataField(body, key, "1");

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
