using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class VariablesPayloadSizeTests
{
    /// <summary>
    /// Raw text on purpose. Spelled as a C# escape the compiler would fold it
    /// into one code unit and the payload under test would never carry the six
    /// characters that make it unreadable.
    /// </summary>
    private const string LoneSurrogateEscape = @"\ud800";

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void An_absent_payload_and_a_json_null_are_always_admitted(string? json)
        => VariablesPayloadSize.Assess(json is null ? null : Parse(json))
            .ShouldBe(VariablesPayloadVerdict.Admitted);

    [Fact]
    public void The_ceiling_admits_the_payload_that_reaches_it_and_refuses_the_next_byte()
    {
        JsonElement atTheCeiling = PayloadOfExactly(VariablesPayloadSize.MaxBytes);
        JsonElement oneByteOver = PayloadOfExactly(VariablesPayloadSize.MaxBytes + 1);

        CompactJsonSize.Measure(atTheCeiling).ByteCount.ShouldBe(VariablesPayloadSize.MaxBytes);
        VariablesPayloadSize.Assess(atTheCeiling).ShouldBe(VariablesPayloadVerdict.Admitted);
        VariablesPayloadSize.Assess(oneByteOver).ShouldBe(VariablesPayloadVerdict.AboveCeiling);
    }

    [Fact]
    public void Text_buried_in_an_array_deep_in_the_payload_counts_like_any_other()
    {
        // What the ceiling bounds is the allowlist scan, which walks every
        // string value at any depth; a measure that stopped at the first level
        // would bound the wrong thing.
        var blob = new string('x', VariablesPayloadSize.MaxBytes);

        VariablesPayloadSize.Assess(Parse(JsonSerializer.Serialize(new
        {
            order = new { items = new[] { new { note = blob } } },
        }))).ShouldBe(VariablesPayloadVerdict.AboveCeiling);
    }

    [Fact]
    public void A_payload_whose_escape_names_no_character_is_refused_as_unreadable_and_never_thrown()
    {
        // The premise, asserted rather than assumed: the payload is legal JSON
        // text and the reader accepts it. That is the whole shape of the
        // fault. Without this assertion a payload that never parsed would let
        // the rest of the test pass while proving nothing.
        using var document = JsonDocument.Parse($$"""{"orderId":"{{LoneSurrogateEscape}}"}""");
        JsonElement payload = document.RootElement.Clone();
        payload.ValueKind.ShouldBe(JsonValueKind.Object);

        VariablesPayloadVerdict verdict = Should.NotThrow(
            () => VariablesPayloadSize.Assess(payload));

        verdict.ShouldBe(VariablesPayloadVerdict.Unreadable);
    }

    [Fact]
    public void An_ordinary_payload_stays_admitted_and_an_oversized_one_is_still_refused_for_its_size()
    {
        // The falsifying pair. Without it the unreadable verdict above would
        // also be returned by a rule that refused everything, and the ceiling
        // would have quietly become the reason for every refusal.
        VariablesPayloadSize.Assess(Parse("""{"orderId":"ord-1"}"""))
            .ShouldBe(VariablesPayloadVerdict.Admitted);
        VariablesPayloadSize.Assess(PayloadOfExactly(VariablesPayloadSize.MaxBytes + 1))
            .ShouldBe(VariablesPayloadVerdict.AboveCeiling);
    }

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
