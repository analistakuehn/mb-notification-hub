using System.Text.Json;
using NotificationHub.Api.Infrastructure.Messaging;

namespace NotificationHub.UnitTests.Infrastructure.Messaging;

public sealed class CloudEventOutboxTests
{
    private static readonly DateTimeOffset Moment =
        new(2026, 8, 23, 14, 3, 11, TimeSpan.Zero);

    [Fact]
    public void Build_produces_a_structured_envelope_the_parser_reads_back()
    {
        OutboxAppend append = CloudEventOutbox.Build(Request());

        CloudEventParse parse = CloudEventParser.Parse(append.PayloadJson);

        parse.InvalidReason.ShouldBeNull();
        CloudEvent published = parse.Event.ShouldNotBeNull();
        published.Type.ShouldBe("araia.notification.rejected.v1");
        published.Source.ShouldBe("urn:araia:notification-hub");
        published.Subject.ShouldBe("cus_01J5X9");
        published.Time.ShouldBe(Moment);
        published.Data.GetProperty("reason").GetString().ShouldBe("no-consent");
    }

    [Fact]
    public void Build_routes_the_row_through_the_bus_lane_with_the_type_as_its_header()
    {
        OutboxAppend append = CloudEventOutbox.Build(Request());

        append.Transport.ShouldBe(OutboxTransports.Kafka);
        append.Destination.ShouldBe("notifications.events.v1");
        append.EventType.ShouldBe("araia.notification.rejected.v1");
        append.MessageKey.ShouldBe("cus_01J5X9");
        append.PriorityClass.ShouldBe("transactional");
    }

    [Fact]
    public void Build_carries_the_tracing_context_as_a_header_and_an_extension_attribute()
    {
        OutboxAppend append = CloudEventOutbox.Build(Request() with { Traceparent = "00-abc-def-01" });

        using JsonDocument headers = JsonDocument.Parse(append.HeadersJson);
        headers.RootElement.GetProperty("traceparent").GetString().ShouldBe("00-abc-def-01");
        CloudEventParser.Parse(append.PayloadJson).Event!.Traceparent.ShouldBe("00-abc-def-01");
    }

    [Fact]
    public void Build_omits_the_tracing_context_when_no_activity_is_running()
    {
        OutboxAppend append = CloudEventOutbox.Build(Request());

        append.HeadersJson.ShouldBe("{}");
        CloudEventParser.Parse(append.PayloadJson).Event!.Traceparent.ShouldBeNull();
    }

    [Fact]
    public void Two_events_of_the_same_fact_still_carry_distinct_envelope_identifiers()
    {
        OutboxAppend first = CloudEventOutbox.Build(Request());
        OutboxAppend second = CloudEventOutbox.Build(Request());

        EnvelopeId(first).ShouldNotBe(EnvelopeId(second));
    }

    private static string EnvelopeId(OutboxAppend append)
    {
        using JsonDocument document = JsonDocument.Parse(append.PayloadJson);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static CloudEventAppend Request() => new()
    {
        Destination = "notifications.events.v1",
        Source = "urn:araia:notification-hub",
        Type = "araia.notification.rejected.v1",
        Subject = "cus_01J5X9",
        Time = Moment,
        PriorityClass = "transactional",
        Data = JsonSerializer.SerializeToElement(new { reason = "no-consent" }),
    };
}
