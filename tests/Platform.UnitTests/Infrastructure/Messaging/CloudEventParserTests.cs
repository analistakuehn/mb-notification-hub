using NotificationHub.Api.Infrastructure.Messaging;

namespace NotificationHub.UnitTests.Infrastructure.Messaging;

/// <summary>
/// The parser is the first thing the platform consumer does with a record, and
/// it runs outside the retry the consumer wraps the processor in. Whatever it
/// cannot answer here it answers by throwing, and that throw takes the
/// consuming background service down rather than one record.
/// </summary>
public sealed class CloudEventParserTests
{
    /// <summary>
    /// Raw text on purpose. Spelled as a C# escape the compiler would fold it
    /// into one code unit and the body under test would never carry the six
    /// characters that make it unreadable.
    /// </summary>
    private const string LoneSurrogateEscape = @"\ud800";

    /// <summary>
    /// Seven of them. A property lookup only unescapes a candidate key whose
    /// escaped length reaches the length of the attribute name being sought,
    /// so one escape breaks the short names and leaves the long ones working.
    /// Seven is past every attribute name this parser reads.
    /// </summary>
    private const string PoisonedKey =
        @"\ud800\ud800\ud800\ud800\ud800\ud800\ud800";

    [Fact]
    public void A_body_whose_top_level_key_names_no_character_is_invalid_and_never_throws()
    {
        var body = $$"""
            {
              "{{PoisonedKey}}": 1,
              "specversion": "1.0",
              "id": "evt-1",
              "source": "urn:araia:tests",
              "type": "araia.notification.requested.v1",
              "data": { "code": "123456" }
            }
            """;

        // The premise, asserted rather than assumed: the escape is still six
        // characters in the body handed to the parser.
        body.Contains(LoneSurrogateEscape, StringComparison.Ordinal)
            .ShouldBeTrue("O corpo deve carregar o escape cru.");

        CloudEventParse parse = Should.NotThrow(() => CloudEventParser.Parse(body));

        parse.Event.ShouldBeNull();

        // Its own reason, not the malformed-JSON one: the reader accepted this
        // body, and a diagnosis naming a syntax error would send whoever reads
        // the dead-letter record looking for one that is not there.
        parse.InvalidReason.ShouldBe(CloudEventParser.ReasonUnreadableText);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("traceparent")]
    public void An_attribute_value_that_names_no_character_is_invalid_and_never_throws(string attribute)
    {
        // Not only the key. Every attribute this parser reads as text
        // transcodes it, so a guard over the lookup alone would leave the same
        // throw on the same path under a different body.
        var body = $$"""
            {
              "specversion": "1.0",
              "id": "evt-1",
              "source": "urn:araia:tests",
              "type": "araia.notification.requested.v1",
              "{{attribute}}": "{{LoneSurrogateEscape}}",
              "data": { "code": "123456" }
            }
            """;

        CloudEventParse parse = Should.NotThrow(() => CloudEventParser.Parse(body));

        parse.Event.ShouldBeNull();
        parse.InvalidReason.ShouldBe(CloudEventParser.ReasonUnreadableText);
    }

    [Fact]
    public void An_unreadable_escape_anywhere_in_the_body_is_refused_including_inside_the_data()
    {
        // The guard is deliberately wider than the throw: which read reaches
        // an unreadable escape first depends on the attribute names sought and
        // on their length, so a body where anything is unreadable is refused
        // whole rather than by whichever read happens to run first.
        var body = $$"""
            {
              "specversion": "1.0",
              "id": "evt-1",
              "source": "urn:araia:tests",
              "type": "araia.notification.requested.v1",
              "data": { "code": "{{LoneSurrogateEscape}}" }
            }
            """;

        CloudEventParse parse = Should.NotThrow(() => CloudEventParser.Parse(body));

        parse.Event.ShouldBeNull();
        parse.InvalidReason.ShouldBe(CloudEventParser.ReasonUnreadableText);
    }

    [Fact]
    public void An_ordinary_envelope_still_parses_and_carries_its_attributes_through()
    {
        // The falsifying half. Without it the three refusals above would also
        // be produced by a parser that refused every record, and the consumer
        // would dead-letter the whole topic instead of the unreadable records.
        var body = """
            {
              "specversion": "1.0",
              "id": "evt-1",
              "source": "urn:araia:tests",
              "type": "araia.notification.requested.v1",
              "subject": "cus_1",
              "unknownFutureAttribute": "ignored",
              "data": { "code": "123456" }
            }
            """;

        CloudEventParse parse = CloudEventParser.Parse(body);

        parse.InvalidReason.ShouldBeNull();
        parse.Event.ShouldNotBeNull();
        parse.Event.Id.ShouldBe("evt-1");
        parse.Event.Type.ShouldBe("araia.notification.requested.v1");
        parse.Event.Subject.ShouldBe("cus_1");
    }

    [Fact]
    public void A_body_that_is_not_json_at_all_keeps_its_own_reason()
    {
        // The two faults stay apart. A body the reader rejects is malformed
        // JSON; a body the reader accepts and nothing can transcode is not,
        // and collapsing them would lose the only diagnosis that tells an
        // operator which of the two happened.
        CloudEventParser.Parse("{ not json").InvalidReason
            .ShouldBe(CloudEventParser.ReasonMalformedJson);
    }
}
