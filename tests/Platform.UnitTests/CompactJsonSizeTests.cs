using System.Text.Json;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests;

public sealed class CompactJsonSizeTests
{
    /// <summary>
    /// The escape is written as raw text on purpose. Spelled as a C# escape
    /// the compiler would fold it into one code unit and the payload under
    /// test would never carry the six characters that make it unreadable.
    /// </summary>
    private const string LoneSurrogateEscape = @"\ud800";

    /// <summary>
    /// Raw text for the same reason: inside a raw string literal these six
    /// characters stay a JSON escape, which is how a producer may spell an
    /// accented character and still send the same payload.
    /// </summary>
    private const string EscapedATilde = @"\u00e3";

    /// <summary>Raw text again: a pair of escapes that together name one character.</summary>
    private const string PairedSurrogateEscape = @"\ud83d\ude00";

    [Fact]
    public void The_same_payload_measures_the_same_however_its_writer_spelled_it()
    {
        // The three spellings are the same payload: indented, compact, and
        // with the accented characters escaped. Whichever door it arrives
        // through, the measure has to answer the same, or one door admits what
        // another then refuses.
        var indented = Measure("""
            {
                "cidade" : "São Paulo"
            }
            """);
        var compact = Measure("""{"cidade":"São Paulo"}""");
        var escaped = Measure($$"""{"cidade":"S{{EscapedATilde}}o Paulo"}""");

        compact.ShouldBe(indented);
        escaped.ShouldBe(indented);
    }

    [Fact]
    public void The_measure_counts_utf8_bytes_and_not_characters()
    {
        // 'ã' is one character and two bytes: a measure over characters would
        // let a payload of accented text through at well above any ceiling.
        Measure("""{"a":"ã"}""").ShouldBe(Measure("""{"a":"a"}""") + 1);
    }

    [Fact]
    public void Text_buried_in_an_array_deep_in_the_payload_counts_like_any_other()
    {
        // What a ceiling bounds is a walk over every value at any depth; a
        // measure that stopped at the first level would bound the wrong thing.
        var blob = new string('x', 4_096);

        Measure(JsonSerializer.Serialize(new
        {
            order = new { items = new[] { new { note = blob } } },
        })).ShouldBeGreaterThan(4_096);
    }

    [Fact]
    public void A_payload_whose_escape_names_no_character_is_reported_unreadable_and_never_thrown()
    {
        // The premise, asserted rather than assumed: the payload is legal JSON
        // text and the reader accepts it. Without this the rest of the test
        // would prove nothing, because a payload that never parsed would reach
        // the measure in no shape at all.
        using var document = JsonDocument.Parse($$"""{"v":"{{LoneSurrogateEscape}}"}""");
        document.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);

        CompactJsonSize.Outcome measured = Should.NotThrow(
            () => CompactJsonSize.Measure(document.RootElement));

        measured.IsReadable.ShouldBeFalse();
    }

    [Fact]
    public void An_escape_that_names_no_character_is_found_wherever_it_sits_in_the_payload()
    {
        // Every position the transcoding walks: a value at depth, an array
        // element, and a property name. A guard that covered only one of them
        // would leave the others taking the caller down.
        using var inNestedValue = JsonDocument.Parse(
            $$$"""{"order":{"note":"{{{LoneSurrogateEscape}}}"}}""");
        using var inArrayElement = JsonDocument.Parse(
            $$"""{"items":["ok","{{LoneSurrogateEscape}}"]}""");
        using var inPropertyName = JsonDocument.Parse(
            $$"""{"{{LoneSurrogateEscape}}":"ok"}""");

        CompactJsonSize.Measure(inNestedValue.RootElement).IsReadable.ShouldBeFalse();
        CompactJsonSize.Measure(inArrayElement.RootElement).IsReadable.ShouldBeFalse();
        CompactJsonSize.Measure(inPropertyName.RootElement).IsReadable.ShouldBeFalse();
    }

    [Fact]
    public void A_paired_surrogate_is_a_character_and_measures_like_one()
    {
        // The falsifying half of the rule above: what is refused is an escape
        // that names no character, never an escaped character. A guard that
        // refused every escaped surrogate would refuse every emoji.
        using var escaped = JsonDocument.Parse(
            $$"""{"v":"{{PairedSurrogateEscape}}"}""");
        using var literal = JsonDocument.Parse("""{"v":"😀"}""");

        CompactJsonSize.Outcome measured = CompactJsonSize.Measure(escaped.RootElement);

        measured.IsReadable.ShouldBeTrue();

        // The two spellings are one payload and measure alike, which is the
        // claim; the number itself is the encoder's business and not this
        // test's, so it is asserted against the other spelling and not
        // against a constant.
        measured.ByteCount.ShouldBe(CompactJsonSize.Measure(literal.RootElement).ByteCount);
    }

    [Fact]
    public void An_uninitialized_outcome_reads_as_unreadable()
    {
        // The default of the struct is the closed answer, so a caller that
        // lets one through uninitialized refuses the payload instead of
        // admitting it as an empty one.
        default(CompactJsonSize.Outcome).IsReadable.ShouldBeFalse();
    }

    private static long Measure(string json)
    {
        using var document = JsonDocument.Parse(json);
        CompactJsonSize.Outcome measured = CompactJsonSize.Measure(document.RootElement);
        measured.IsReadable.ShouldBeTrue();
        return measured.ByteCount;
    }
}
