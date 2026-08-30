using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.Notifications;

public sealed class MetadataPayloadSizeTests
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
    public void Absent_metadata_and_a_json_null_are_always_admitted(string? json)
        => MetadataPayloadSize.Assess(json is null ? null : Parse(json))
            .ShouldBe(MetadataPayloadVerdict.Admitted);

    [Fact]
    public void The_ceiling_admits_the_payload_that_reaches_it_and_refuses_the_next_byte()
    {
        JsonElement atTheCeiling = PayloadOfExactly(MetadataPayloadSize.MaxBytes);
        JsonElement oneByteOver = PayloadOfExactly(MetadataPayloadSize.MaxBytes + 1);

        CompactJsonSize.Measure(atTheCeiling).ByteCount.ShouldBe(MetadataPayloadSize.MaxBytes);
        MetadataPayloadSize.Assess(atTheCeiling).ShouldBe(MetadataPayloadVerdict.Admitted);
        MetadataPayloadSize.Assess(oneByteOver).ShouldBe(MetadataPayloadVerdict.AboveCeiling);
    }

    [Fact]
    public void Sorting_the_keys_reorders_the_bytes_the_hash_writes_without_changing_how_many()
    {
        // The claim the measure rests on: it counts the compact form, while
        // the hash writes the canonical one. The two forms differ only in the
        // order of the members, so the count is exact and not an estimate.
        JsonElement payload = Parse("""{"zulu":1,"alfa":{"yankee":2,"bravo":3}}""");

        CompactJsonSize.Measure(payload).ByteCount
            .ShouldBe(CanonicalJson.CanonicalBytes(payload).LongLength);
    }

    [Fact]
    public void Context_buried_deep_in_the_metadata_counts_like_any_other()
    {
        // What the ceiling bounds is a canonicalization that recurses through
        // every level; a measure that stopped at the first would bound nothing.
        var blob = new string('x', MetadataPayloadSize.MaxBytes);

        MetadataPayloadSize.Assess(Parse(JsonSerializer.Serialize(new
        {
            trace = new { spans = new[] { new { note = blob } } },
        }))).ShouldBe(MetadataPayloadVerdict.AboveCeiling);
    }

    [Fact]
    public void The_metadata_ceiling_stays_below_the_ceiling_the_catalog_publishes_for_variables()
    {
        // The ratio is the decision, not an accident of two independent
        // numbers: metadata is bounded lower because the hub renders it never
        // and stores it nowhere at ingestion, so it buys less than variables
        // buy while the hash pays for every byte of it twice.
        MetadataPayloadSize.MaxBytes.ShouldBeLessThan(VariablesPayloadLimit.MaxBytes);
    }

    [Fact]
    public void Metadata_whose_escape_names_no_character_is_refused_as_unreadable_and_never_thrown()
    {
        // The premise, asserted rather than assumed: the payload is legal JSON
        // text and the reader accepts it. That is the whole shape of the
        // fault. Without this assertion a payload that never parsed would let
        // the rest of the test pass while proving nothing.
        using var document = JsonDocument.Parse($$"""{"origin":"{{LoneSurrogateEscape}}"}""");
        JsonElement payload = document.RootElement.Clone();
        payload.ValueKind.ShouldBe(JsonValueKind.Object);

        MetadataPayloadVerdict verdict = Should.NotThrow(
            () => MetadataPayloadSize.Assess(payload));

        verdict.ShouldBe(MetadataPayloadVerdict.Unreadable);
    }

    [Fact]
    public void Ordinary_metadata_stays_admitted_and_oversized_metadata_is_still_refused_for_its_size()
    {
        // The falsifying pair. Without it the unreadable verdict above would
        // also be returned by a rule that refused everything, and the ceiling
        // would have quietly become the reason for every refusal.
        MetadataPayloadSize.Assess(Parse("""{"origin":"mobile"}"""))
            .ShouldBe(MetadataPayloadVerdict.Admitted);
        MetadataPayloadSize.Assess(PayloadOfExactly(MetadataPayloadSize.MaxBytes + 1))
            .ShouldBe(MetadataPayloadVerdict.AboveCeiling);
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
