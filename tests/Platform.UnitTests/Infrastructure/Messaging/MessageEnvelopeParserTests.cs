using System.Text.Json;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;

namespace NotificationHub.UnitTests.Infrastructure.Messaging;

public sealed class MessageEnvelopeParserTests
{
    private static string ValidBody(Guid? messageId = null) => JsonSerializer.Serialize(new
    {
        messageId = messageId ?? Guid.NewGuid(),
        type = "notification.accepted",
        schemaVersion = 1,
        occurredAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
        traceparent = "00-abc-def-01",
        priorityClass = "critical",
        payload = new { notificationId = Guid.NewGuid() },
    });

    [Fact]
    public void A_ratified_envelope_parses_with_every_field()
    {
        var id = Guid.NewGuid();

        MessageEnvelopeParse parse = MessageEnvelopeParser.Parse(ValidBody(id));

        parse.InvalidReason.ShouldBeNull();
        MessageEnvelope envelope = parse.Envelope.ShouldNotBeNull();
        envelope.MessageId.ShouldBe(id);
        envelope.Type.ShouldBe("notification.accepted");
        envelope.SchemaVersion.ShouldBe(1);
        envelope.OccurredAt.ShouldBe(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        envelope.Traceparent.ShouldBe("00-abc-def-01");
        envelope.PriorityClass.ShouldBe("critical");
        envelope.Payload.TryGetProperty("notificationId", out _).ShouldBeTrue();
    }

    [Fact]
    public void A_body_that_is_not_json_is_permanently_invalid()
        => MessageEnvelopeParser.Parse("not json").InvalidReason
            .ShouldBe(MessageEnvelopeParser.ReasonMalformedJson);

    [Fact]
    public void A_json_array_body_is_permanently_invalid()
        => MessageEnvelopeParser.Parse("[]").InvalidReason
            .ShouldBe(MessageEnvelopeParser.ReasonNotAnObject);

    [Theory]
    [InlineData("messageId", MessageEnvelopeParser.ReasonMissingMessageId)]
    [InlineData("type", MessageEnvelopeParser.ReasonMissingType)]
    [InlineData("schemaVersion", MessageEnvelopeParser.ReasonMissingSchemaVersion)]
    [InlineData("payload", MessageEnvelopeParser.ReasonMissingPayload)]
    public void A_missing_required_field_is_permanently_invalid(string field, string expectedReason)
    {
        using var document = JsonDocument.Parse(ValidBody());
        var stripped = new Dictionary<string, JsonElement>();
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Name != field)
            {
                stripped[property.Name] = property.Value;
            }
        }

        MessageEnvelopeParse parse = MessageEnvelopeParser.Parse(JsonSerializer.Serialize(stripped));

        parse.Envelope.ShouldBeNull();
        parse.InvalidReason.ShouldBe(expectedReason);
    }

    [Fact]
    public void A_non_guid_message_id_is_permanently_invalid()
        => MessageEnvelopeParser
            .Parse("""{"messageId":"not-a-guid","type":"x","schemaVersion":1,"payload":{}}""")
            .InvalidReason
            .ShouldBe(MessageEnvelopeParser.ReasonMissingMessageId);

    [Fact]
    public void Optional_fields_may_be_absent()
    {
        var body = JsonSerializer.Serialize(new
        {
            messageId = Guid.NewGuid(),
            type = "contact.changed",
            schemaVersion = 1,
            payload = new { recipientId = "abc" },
        });

        MessageEnvelopeParse parse = MessageEnvelopeParser.Parse(body);

        MessageEnvelope envelope = parse.Envelope.ShouldNotBeNull();
        envelope.OccurredAt.ShouldBeNull();
        envelope.Traceparent.ShouldBeNull();
        envelope.PriorityClass.ShouldBeNull();
    }

    [Fact]
    public void The_payload_survives_the_document_disposal()
    {
        MessageEnvelopeParse parse = MessageEnvelopeParser.Parse(ValidBody());

        // The clone must stay readable after Parse disposed its document.
        parse.Envelope!.Payload.GetProperty("notificationId").GetString().ShouldNotBeNull();
    }
}
