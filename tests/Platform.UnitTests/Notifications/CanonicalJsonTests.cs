using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.UnitTests.Notifications;

/// <summary>
/// The canonical form feeds the idempotency payload hash, and that hash is
/// compared against values already stored, so these bytes are a persisted
/// contract rather than an implementation detail. Every case below pins the
/// exact output in hex: a rewrite that is faster and answers one byte
/// differently has silently turned every stored registration into a conflict,
/// and only a test that reads the bytes can say so.
/// </summary>
public sealed class CanonicalJsonTests
{
    [Theory]

    // Ordinal key order over the whole range, duplicate keys collapsed to the
    // last occurrence, arrays left in their order, scalars written as parsed.
    [InlineData(
        """{"B":1,"a":2,"A":3,"b":4,"_":5,"0":6}""",
        "7B2230223A362C2241223A332C2242223A312C225F223A352C2261223A322C2262223A347D")]
    [InlineData(
        """{"k":1,"a":2,"k":3,"a":4,"k":5}""",
        "7B2261223A342C226B223A357D")]
    [InlineData(
        """{"b":{"z":[3,1,2],"a":{"y":1,"x":2}},"a":null}""",
        "7B2261223A6E756C6C2C2262223A7B2261223A7B2278223A322C2279223A317D2C227A223A5B332C312C325D7D7D")]
    [InlineData(
        """{"ab":1,"a":2,"abc":3,"":4}""",
        "7B22223A342C2261223A322C226162223A312C22616263223A337D")]
    [InlineData(
        """{"m":1e3,"n":1.0,"o":-0.0,"p":123456789012345678901234567890}""",
        "7B226D223A3165332C226E223A312E302C226F223A2D302E302C2270223A3132333435363738393031323334353637383930313233343536373839307D")]
    [InlineData(
        """{"a":{},"b":[],"c":""}""",
        "7B2261223A7B7D2C2262223A5B5D2C2263223A22227D")]
    [InlineData(
        """{"São":1,"Sao":2,"Sz":3}""",
        "7B2253616F223A322C22537A223A332C2253C3A36F223A317D")]
    public void The_canonical_bytes_of_a_payload_are_pinned(string json, string expectedHex)
        => Convert.ToHexString(CanonicalJson.CanonicalBytes(Parse(json))).ShouldBe(expectedHex);

    [Fact]
    public void Keys_are_ordered_by_utf16_code_unit_and_not_by_their_utf8_bytes()
    {
        // The one ordering case that separates the two, and the reason this
        // test exists as its own fact. A key on a supplementary plane is a
        // surrogate pair in UTF-16, so it starts at U+D83D and sorts BEFORE
        // U+E001; encoded as UTF-8 it starts at 0xF0 and would sort AFTER the
        // 0xEE of U+E001. Sorting the encoded name instead of the string
        // therefore reverses these two members and rewrites the hash of every
        // payload that carries such a key.
        var canonical = Encoding.UTF8.GetString(
            CanonicalJson.CanonicalBytes(Parse("{\"\uD83D\uDE00\":1,\"\uE001\":2,\"a\":3}")));

        // The writer spells both keys as escapes, so the canonical text
        // carries them literally and the order is readable inside it.
        canonical.ShouldBe("{\"a\":3,\"\\uD83D\\uDE00\":1,\"\\uE001\":2}");
        canonical.IndexOf("D83D", StringComparison.Ordinal)
            .ShouldBeLessThan(canonical.IndexOf("E001", StringComparison.Ordinal));
    }

    [Fact]
    public void A_generated_corpus_matches_the_form_the_stored_hashes_were_written_with()
    {
        // The pinned cases above are the shapes a reader can check by eye. This
        // one is the breadth: every payload the generator can produce is
        // canonicalized twice, once by the reference form below and once by the
        // implementation in use, and the two answer the same bytes or the
        // corpus names the payload that broke.
        for (var seed = 0; seed < 400; seed++)
        {
            JsonElement payload = GeneratePayload(new Random(seed), depth: 0);

            Convert.ToHexString(CanonicalJson.CanonicalBytes(payload))
                .ShouldBe(Convert.ToHexString(ReferenceCanonicalBytes(payload)), $"seed {seed}");
        }
    }

    [Fact]
    public void Canonicalizing_an_object_does_not_allocate_a_container_per_level()
    {
        // The guard on the shape of the cost, not on the clock. Time on this
        // path swings by a factor of two between runs on one machine, which is
        // how a timing assertion fabricates a failure and gets itself deleted;
        // allocated bytes over a fixed payload are deterministic and answered
        // the same on every run measured, in Debug and in Release alike. What
        // it defends is the collection strategy: holding members in a sorted
        // dictionary costs a tree node per member at every depth, measured at
        // 113,352 bytes per call over this payload against the 55,560 it costs
        // now. The ceiling sits between the two, with room above the current
        // cost, so a return to a per-level container fails here while an
        // ordinary refactor has somewhere to move.
        JsonElement payload = Parse(ManyMembers());

        CanonicalJson.CanonicalBytes(payload);
        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 20;
        for (var i = 0; i < iterations; i++)
        {
            CanonicalJson.CanonicalBytes(payload);
        }

        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        perCall.ShouldBeLessThan(90_000);
    }

    /// <summary>
    /// Two hundred members, each an object of its own, so the collection runs
    /// at both levels and the count is what a producer payload of a few
    /// kilobytes actually looks like.
    /// </summary>
    private static string ManyMembers()
    {
        var builder = new StringBuilder("{");
        for (var i = 0; i < 200; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $"\"k{i:D6}\":{{\"a\":\"v{i:D6}\",\"b\":{i}}}");
        }

        return builder.Append('}').ToString();
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>
    /// Keys drawn to sit on the boundaries the ordering has to answer for:
    /// case, prefix, digits, punctuation below and above the letters, accented
    /// text, a private-use character, and a supplementary plane character.
    /// </summary>
    private static readonly string[] Keys =
    [
        "a", "A", "b", "B", "ab", "abc", "", "0", "9", "_", "-", "~", "!",
        "São", "Sao", "sao", "", "😀", "�", " ", "\t",
        "chave", "Chave", "CHAVE", "k1", "k10", "k2",
    ];

    private static JsonElement GeneratePayload(Random random, int depth)
        => Parse(GenerateJson(random, depth));

    private static string GenerateJson(Random random, int depth)
    {
        // Depth is bounded so the generator terminates; the shapes that matter
        // for ordering are all reachable within it.
        var choice = depth >= 3 ? random.Next(4, 9) : random.Next(0, 9);
        switch (choice)
        {
            case 0:
            case 1:
                var members = random.Next(0, 7);
                var builder = new StringBuilder("{");
                for (var i = 0; i < members; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    // Duplicate keys are drawn on purpose: the same small pool
                    // is sampled with replacement, so a payload that collapses
                    // members appears on its own.
                    builder.Append(JsonSerializer.Serialize(Keys[random.Next(Keys.Length)]));
                    builder.Append(':');
                    builder.Append(GenerateJson(random, depth + 1));
                }

                return builder.Append('}').ToString();
            case 2:
            case 3:
                var items = random.Next(0, 5);
                var array = new StringBuilder("[");
                for (var i = 0; i < items; i++)
                {
                    if (i > 0)
                    {
                        array.Append(',');
                    }

                    array.Append(GenerateJson(random, depth + 1));
                }

                return array.Append(']').ToString();
            case 4: return JsonSerializer.Serialize(Keys[random.Next(Keys.Length)]);
            case 5: return random.Next(-1000, 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case 6: return "true";
            case 7: return "false";
            default: return "null";
        }
    }

    /// <summary>
    /// The canonical form as it stood when the hashes now in the database were
    /// written: a sorted dictionary keyed by the member name under
    /// <see cref="StringComparer.Ordinal"/>, which is UTF-16 code-unit order,
    /// with a later duplicate replacing an earlier one. It is kept here, and
    /// only here, as the reference the implementation is held against.
    /// </summary>
    private static byte[] ReferenceCanonicalBytes(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteReference(element, writer);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteReference(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    properties[property.Name] = property.Value;
                }

                foreach ((var name, JsonElement value) in properties)
                {
                    writer.WritePropertyName(name);
                    WriteReference(value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteReference(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
