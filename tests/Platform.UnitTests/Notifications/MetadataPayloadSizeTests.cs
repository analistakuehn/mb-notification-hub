using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Notifications;

public sealed class MetadataPayloadSizeTests
{
    [Fact]
    public void The_same_metadata_measures_the_same_however_its_writer_spelled_it()
    {
        // The three spellings are one payload: indented, compact, and with the
        // accented characters escaped. Indentation and escaping are the
        // producer's choice, so a measure that read the arriving text would
        // refuse one request and admit the same one written differently.
        var indented = Measure("""
            {
                "origem" : "São Paulo"
            }
            """);
        var compact = Measure("""{"origem":"São Paulo"}""");
        var escaped = Measure("""{"origem":"S\u00e3o Paulo"}""");

        compact.ShouldBe(indented);
        escaped.ShouldBe(indented);
    }

    [Fact]
    public void The_measure_counts_utf8_bytes_and_not_characters()
    {
        // 'ã' is one character and two bytes: a measure over characters would
        // let accented context through at well above the ceiling.
        Measure("""{"a":"ã"}""").ShouldBe(Measure("""{"a":"a"}""") + 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void Absent_metadata_and_a_json_null_are_never_above_the_ceiling(string? json)
        => MetadataPayloadSize.ExceedsMaxBytes(json is null ? null : Parse(json)).ShouldBeFalse();

    [Fact]
    public void The_ceiling_admits_the_payload_that_reaches_it_and_refuses_the_next_byte()
    {
        JsonElement atTheCeiling = PayloadOfExactly(MetadataPayloadSize.MaxBytes);
        JsonElement oneByteOver = PayloadOfExactly(MetadataPayloadSize.MaxBytes + 1);

        MetadataPayloadSize.CompactByteCount(atTheCeiling).ShouldBe(MetadataPayloadSize.MaxBytes);
        MetadataPayloadSize.ExceedsMaxBytes(atTheCeiling).ShouldBeFalse();
        MetadataPayloadSize.ExceedsMaxBytes(oneByteOver).ShouldBeTrue();
    }

    [Fact]
    public void Sorting_the_keys_reorders_the_bytes_the_hash_writes_without_changing_how_many()
    {
        // The claim the measure rests on: it counts the compact form, while
        // the hash writes the canonical one. The two forms differ only in the
        // order of the members, so the count is exact and not an estimate.
        JsonElement payload = Parse("""{"zulu":1,"alfa":{"yankee":2,"bravo":3}}""");

        MetadataPayloadSize.CompactByteCount(payload)
            .ShouldBe(CanonicalJson.CanonicalBytes(payload).LongLength);
    }

    [Fact]
    public void Context_buried_deep_in_the_metadata_counts_like_any_other()
    {
        // What the ceiling bounds is a canonicalization that recurses through
        // every level; a measure that stopped at the first would bound nothing.
        var blob = new string('x', MetadataPayloadSize.MaxBytes);

        MetadataPayloadSize.ExceedsMaxBytes(Parse(JsonSerializer.Serialize(new
        {
            trace = new { spans = new[] { new { note = blob } } },
        }))).ShouldBeTrue();
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

    private static long Measure(string json) => MetadataPayloadSize.CompactByteCount(Parse(json));

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
