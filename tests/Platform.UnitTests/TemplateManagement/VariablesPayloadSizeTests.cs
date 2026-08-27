using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class VariablesPayloadSizeTests
{
    [Fact]
    public void The_same_payload_measures_the_same_however_its_writer_spelled_it()
    {
        // The three spellings are the same payload: indented, compact, and
        // with the accented characters escaped. Whichever door it arrives
        // through, the ceiling has to answer the same, or the ingestion admits
        // what the render then refuses.
        var indented = Measure("""
            {
                "cidade" : "São Paulo"
            }
            """);
        var compact = Measure("""{"cidade":"São Paulo"}""");
        var escaped = Measure("""{"cidade":"S\u00e3o Paulo"}""");

        compact.ShouldBe(indented);
        escaped.ShouldBe(indented);
    }

    [Fact]
    public void The_measure_counts_utf8_bytes_and_not_characters()
    {
        // 'ã' is one character and two bytes: a measure over characters would
        // let a payload of accented text through at well above the ceiling.
        Measure("""{"a":"ã"}""").ShouldBe(Measure("""{"a":"a"}""") + 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void An_absent_payload_and_a_json_null_are_never_above_the_ceiling(string? json)
        => VariablesPayloadSize.ExceedsMaxBytes(json is null ? null : Parse(json)).ShouldBeFalse();

    [Fact]
    public void The_ceiling_admits_the_payload_that_reaches_it_and_refuses_the_next_byte()
    {
        JsonElement atTheCeiling = PayloadOfExactly(VariablesPayloadSize.MaxBytes);
        JsonElement oneByteOver = PayloadOfExactly(VariablesPayloadSize.MaxBytes + 1);

        VariablesPayloadSize.CompactByteCount(atTheCeiling).ShouldBe(VariablesPayloadSize.MaxBytes);
        VariablesPayloadSize.ExceedsMaxBytes(atTheCeiling).ShouldBeFalse();
        VariablesPayloadSize.ExceedsMaxBytes(oneByteOver).ShouldBeTrue();
    }

    [Fact]
    public void Text_buried_in_an_array_deep_in_the_payload_counts_like_any_other()
    {
        // What the ceiling bounds is the allowlist scan, which walks every
        // string value at any depth; a measure that stopped at the first level
        // would bound the wrong thing.
        var blob = new string('x', VariablesPayloadSize.MaxBytes);

        VariablesPayloadSize.ExceedsMaxBytes(Parse(JsonSerializer.Serialize(new
        {
            order = new { items = new[] { new { note = blob } } },
        }))).ShouldBeTrue();
    }

    private static long Measure(string json) => VariablesPayloadSize.CompactByteCount(Parse(json));

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>
    /// A single-property object whose compact form occupies exactly
    /// <paramref name="bytes"/>, so the boundary can be asserted on the number
    /// itself instead of on an estimate around it.
    /// </summary>
    private static JsonElement PayloadOfExactly(int bytes)
    {
        // {"v":""} is eight bytes, and every added character is one more.
        var filler = new string('x', bytes - 8);
        return Parse($$"""{"v":"{{filler}}"}""");
    }
}
