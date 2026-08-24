using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;
using NotificationHub.Api.Modules.Notifications.Integration.V1;

namespace NotificationHub.UnitTests.Notifications.Ingress;

/// <summary>
/// The redaction is the mitigation of a control that would otherwise defeat
/// itself: refusing a request for carrying a secret and then copying that
/// secret onto a topic with fourteen times the retention. These tests hold the
/// line that no value survives the copy.
/// </summary>
public sealed class IngressDeadLetterRedactionTests
{
    private const string ApplicationSentinel = "sentinel-application-must-not-reach-dlt";
    private const string ClassSentinel = "sentinel-class-must-not-reach-dlt";
    private const string IdempotencySentinel = "sentinel-idempotency-must-not-reach-dlt";
    private const string LiteralSecret = "incident-secret-must-not-reach-dlt";
    private const string MetadataSentinel = "sentinel-metadata-must-not-reach-dlt";
    private const string OriginalKeySentinel = "sentinel-record-key-must-not-reach-dlt";
    private const string SourceSentinel = "sentinel-source-must-not-reach-dlt";
    private const string SubjectSentinel = "sentinel-subject-must-not-reach-dlt";
    private const string TraceparentSentinel = "sentinel-traceparent-must-not-reach-dlt";
    private const string VariablesSentinel = "sentinel-variables-must-not-reach-dlt";

    private const string LogicalProducer = "kyc-service";
    private const string SourceTopic = "notifications.requested.kyc";

    private static readonly IReadOnlyList<string> UntrustedSentinels =
    [
        OriginalKeySentinel,
        TraceparentSentinel,
        ApplicationSentinel,
        ClassSentinel,
        IdempotencySentinel,
        MetadataSentinel,
        SourceSentinel,
        SubjectSentinel,
        VariablesSentinel,
    ];

    private const string Body = """
        {
          "specversion": "1.0",
          "id": "evt-1",
          "source": "urn:araia:kyc-service",
          "type": "araia.notification.requested.v1",
          "subject": "cus_01",
          "data": {
            "application": "araia-cambio",
            "recipientId": "cus_01",
            "idempotencyKey": "key-1",
            "class": "critical",
            "templateKey": "auth.otp",
            "variables": { "code": "incident-secret-must-not-reach-dlt", "expiresIn": "5" }
          }
        }
        """;

    private static string PreTrustBody => $$"""
        {
          "specversion": "1.0",
          "id": "evt-pre-trust",
          "source": "{{SourceSentinel}}",
          "type": "araia.notification.requested.v1",
          "subject": "{{SubjectSentinel}}",
          "traceparent": "{{TraceparentSentinel}}",
          "metadata": { "diagnostic": "{{MetadataSentinel}}" },
          "data": {
            "application": "{{ApplicationSentinel}}",
            "recipientId": "cus_01",
            "idempotencyKey": "{{IdempotencySentinel}}",
            "class": "{{ClassSentinel}}",
            "templateKey": "auth.otp",
            "variables": { "code": "{{VariablesSentinel}}" }
          }
        }
        """;

    [Fact]
    public void Redaction_replaces_the_variables_with_the_declared_names()
    {
        var redacted = IngressDeadLetterWriter.RedactVariables(Body, ["code"]);

        using JsonDocument document = JsonDocument.Parse(redacted);
        JsonElement variables = document.RootElement.GetProperty("data").GetProperty("variables");
        variables.ValueKind.ShouldBe(JsonValueKind.Array);
        variables.EnumerateArray().Select(item => item.GetString()).ShouldBe(["code"]);
    }

    [Fact]
    public void Redaction_carries_no_variable_value_anywhere_in_the_body()
    {
        var redacted = IngressDeadLetterWriter.RedactVariables(Body, ["code", "expiresIn"]);

        redacted.ShouldNotContain(LiteralSecret);
        // Falsification: the untouched body does carry it, so the assertion
        // above is measuring the redaction and not the absence of the string.
        Body.ShouldContain(LiteralSecret);
    }

    [Fact]
    public void Redaction_keeps_the_diagnostic_fields_the_producer_needs()
    {
        var redacted = IngressDeadLetterWriter.RedactVariables(Body, ["code"]);

        using JsonDocument document = JsonDocument.Parse(redacted);
        JsonElement data = document.RootElement.GetProperty("data");
        data.GetProperty("templateKey").GetString().ShouldBe("auth.otp");
        data.GetProperty("idempotencyKey").GetString().ShouldBe("key-1");
        document.RootElement.GetProperty("type").GetString().ShouldBe("araia.notification.requested.v1");
    }

    [Fact]
    public void An_unparseable_body_loses_everything_but_the_declared_names()
    {
        var redacted = IngressDeadLetterWriter.RedactVariables("{ not json at all", ["code"]);

        redacted.ShouldNotContain("not json at all");
        using JsonDocument document = JsonDocument.Parse(redacted);
        document.RootElement.GetProperty("redactedVariables")
            .EnumerateArray().Select(item => item.GetString()).ShouldBe(["code"]);
    }

    [Fact]
    public void A_body_without_a_data_section_loses_everything_but_the_declared_names()
    {
        var redacted = IngressDeadLetterWriter.RedactVariables("""{"specversion":"1.0","data":"483920"}""", ["code"]);

        redacted.ShouldNotContain("483920");
    }

    [Theory]
    [InlineData(NotificationRejectionReasons.PayloadInvalid)]
    [InlineData(NotificationRejectionReasons.EventTypeUnsupported)]
    [InlineData(NotificationRejectionReasons.ProducerDisabled)]
    [InlineData(NotificationRejectionReasons.ProducerNotAuthorized)]
    public async Task A_pre_trust_refusal_only_publishes_safe_broker_diagnostics(string reason)
    {
        (IngressDeadLetterWriter writer, Func<DeadLetterRecord?> getPublished, List<string> logs) =
            BuildWriter();
        CloudEvent cloudEvent = CloudEventParser.Parse(PreTrustBody).Event.ShouldNotBeNull();
        var context = new KafkaMessageContext
        {
            Topic = SourceTopic,
            Partition = 2,
            Offset = 41,
            Key = OriginalKeySentinel,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DeadLetterHeaders.Traceparent] = TraceparentSentinel,
            },
            Body = PreTrustBody,
            Event = cloudEvent,
        };
        var diagnosis = new DeadLetterDiagnosis
        {
            Reason = reason,
            Producer = LogicalProducer,
            Application = ApplicationSentinel,
            Class = ClassSentinel,
            IdempotencyKey = IdempotencySentinel,
        };

        await writer.ProduceAsync(
            context,
            diagnosis,
            CancellationToken.None);

        DeadLetterRecord record = getPublished().ShouldNotBeNull();
        logs.Count.ShouldBe(1);
        string[] emittedSurfaces =
        [
            record.Key ?? string.Empty,
            record.Body,
            JsonSerializer.Serialize(record.Headers),
            .. logs,
        ];
        UntrustedSentinels.ShouldAllBe(sentinel =>
            emittedSurfaces.All(surface =>
                !surface.Contains(sentinel, StringComparison.Ordinal)));

        record.Key.ShouldBe(LogicalProducer);
        string[] expectedHeaders =
        [
            DeadLetterHeaders.OccurredAt,
            DeadLetterHeaders.Reason,
            DeadLetterHeaders.Redacted,
            DeadLetterHeaders.SourceOffset,
            DeadLetterHeaders.SourcePartition,
            DeadLetterHeaders.SourceTopic,
            IngressDeadLetterWriter.ProducerHeader,
        ];
        IEnumerable<string> actualHeaders = record.Headers.Keys.OrderBy(name => name, StringComparer.Ordinal);
        actualHeaders.ShouldBe(expectedHeaders.OrderBy(name => name, StringComparer.Ordinal));

        using JsonDocument document = JsonDocument.Parse(record.Body);
        string[] expectedBodyProperties =
        [
            DeadLetterHeaders.Reason,
            DeadLetterHeaders.SourceOffset,
            DeadLetterHeaders.SourcePartition,
            DeadLetterHeaders.SourceTopic,
            IngressDeadLetterWriter.ProducerHeader,
        ];
        IEnumerable<string> actualBodyProperties = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);
        actualBodyProperties.ShouldBe(
            expectedBodyProperties.OrderBy(name => name, StringComparer.Ordinal));
        document.RootElement.GetProperty(DeadLetterHeaders.Reason).GetString().ShouldBe(reason);
        document.RootElement.GetProperty(IngressDeadLetterWriter.ProducerHeader)
            .GetString().ShouldBe(LogicalProducer);

        // Falsification: every forbidden value is present in the source record
        // or diagnosis, so an assertion cannot pass because a sentinel was
        // absent from the exercised input.
        context.Key.ShouldBe(OriginalKeySentinel);
        UntrustedSentinels
            .Where(sentinel => sentinel != OriginalKeySentinel)
            .ShouldAllBe(sentinel => context.Body.Contains(sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sensitive_variable_refusal_preserves_diagnostics_and_replaces_variable_values()
    {
        (IngressDeadLetterWriter writer, Func<DeadLetterRecord?> getPublished, _) = BuildWriter();
        KafkaMessageContext context = CreateContext(Body, "cus_01", "traceparent-safe");

        await writer.ProduceAsync(
            context,
            new DeadLetterDiagnosis
            {
                Reason = NotificationRejectionReasons.SensitiveVariablesOnBus,
                Producer = LogicalProducer,
                Application = "araia-cambio",
                Class = "critical",
                IdempotencyKey = "key-1",
                RedactedVariableNames = ["code", "expiresIn"],
            },
            CancellationToken.None);

        DeadLetterRecord record = getPublished().ShouldNotBeNull();
        record.Key.ShouldBe(context.Key);
        record.Body.ShouldNotContain(LiteralSecret);
        record.Body.ShouldContain("auth.otp");
        record.Headers[DeadLetterHeaders.Traceparent].ShouldBe("traceparent-safe");
        record.Headers[IngressDeadLetterWriter.ApplicationHeader].ShouldBe("araia-cambio");
        record.Headers[IngressDeadLetterWriter.ClassHeader].ShouldBe("critical");
        record.Headers[IngressDeadLetterWriter.IdempotencyKeyHeader].ShouldBe("key-1");
    }

    [Fact]
    public async Task A_post_trust_refusal_preserves_the_original_record()
    {
        (IngressDeadLetterWriter writer, Func<DeadLetterRecord?> getPublished, _) = BuildWriter();
        KafkaMessageContext context = CreateContext(Body, "cus_01", "traceparent-safe");

        await writer.ProduceAsync(
            context,
            new DeadLetterDiagnosis
            {
                Reason = NotificationRejectionReasons.TemplateNotFound,
                Producer = LogicalProducer,
                Application = "araia-cambio",
                Class = "critical",
                IdempotencyKey = "key-1",
            },
            CancellationToken.None);

        DeadLetterRecord record = getPublished().ShouldNotBeNull();
        record.Key.ShouldBe(context.Key);
        record.Body.ShouldBe(context.Body);
        record.Headers[DeadLetterHeaders.Traceparent].ShouldBe("traceparent-safe");
        record.Headers[IngressDeadLetterWriter.ApplicationHeader].ShouldBe("araia-cambio");
        record.Headers[IngressDeadLetterWriter.ClassHeader].ShouldBe("critical");
        record.Headers[IngressDeadLetterWriter.IdempotencyKeyHeader].ShouldBe("key-1");
    }

    private static (IngressDeadLetterWriter Writer, Func<DeadLetterRecord?> GetPublished, List<string> Logs)
        BuildWriter()
    {
        DeadLetterRecord? published = null;
        IKafkaDeadLetterProducer producer = Substitute.For<IKafkaDeadLetterProducer>();
        producer.ProduceAsync(
                Arg.Do<DeadLetterRecord>(record => published = record),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var logs = new List<string>();
        ILogger<IngressDeadLetterWriter> logger = Substitute.For<ILogger<IngressDeadLetterWriter>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        logger
            .When(instance => instance.Log(
                LogLevel.Warning,
                Arg.Any<EventId>(),
                Arg.Any<Arg.AnyType>(),
                Arg.Any<Exception?>(),
                Arg.Any<Func<Arg.AnyType, Exception?, string>>()))
            .Do(call => logs.Add(call[2]!.ToString()!));

        var writer = new IngressDeadLetterWriter(
            producer,
            Options.Create(new KafkaIngressOptions
            {
                DeadLetterTopic = "notifications.dead-letter",
            }),
            TimeProvider.System,
            logger);
        return (writer, () => published, logs);
    }

    private static KafkaMessageContext CreateContext(string body, string key, string traceparent)
    {
        CloudEvent cloudEvent = CloudEventParser.Parse(body).Event.ShouldNotBeNull();
        return new KafkaMessageContext
        {
            Topic = SourceTopic,
            Partition = 2,
            Offset = 41,
            Key = key,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DeadLetterHeaders.Traceparent] = traceparent,
            },
            Body = body,
            Event = cloudEvent,
        };
    }
}
