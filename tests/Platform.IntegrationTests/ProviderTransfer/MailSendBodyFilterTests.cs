using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NotificationHub.PerformanceTests.ProviderTransfer;

namespace NotificationHub.IntegrationTests.ProviderTransfer;

/// <summary>
/// The double reads the body without keeping it, which means it does its own
/// scanning of the bytes as they go past. A scanner is only worth what a
/// hostile sample says it is: escaped quotes, a backslash inside a name,
/// padding at the end of the base64, and values split at every awkward
/// boundary the network could produce.
/// </summary>
public sealed class MailSendBodyFilterTests
{
    private const string Marker = "capture-probe";

    [Fact]
    public void A_body_whose_values_all_fit_inline_is_reproduced_byte_for_byte()
    {
        var body = """
            {"subject":"Ele disse \"olá\"","attachments":[{"content":"QUJD",
            "filename":"nota \\ \"final\".pdf","type":"application/pdf","disposition":"attachment"}]}
            """.ReplaceLineEndings(string.Empty);

        MailSendBodyContent content = Read(body, 1, 3, 17);

        Encoding.UTF8.GetString(content.ShrunkJson).ShouldBe(body);
        content.BodyBytes.ShouldBe(Encoding.UTF8.GetByteCount(body));
        content.LargeValues.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(8_190)]
    [InlineData(8_191)]
    [InlineData(8_192)]
    public void A_value_over_the_inline_limit_is_decoded_on_the_way_past_and_replaced_by_a_marker(int rawBytes)
    {
        var raw = Pattern(rawBytes);
        var encoded = Convert.ToBase64String(raw);
        var body = $$"""{"attachments":[{"content":"{{encoded}}","filename":"comprovante.pdf"}]}""";

        MailSendBodyContent content = Read(body, 1, 7, 4_093, 61);

        CapturedLargeValue value = content.LargeValues.ShouldHaveSingleItem();
        value.DecodedSuccessfully.ShouldBeTrue();
        value.DecodedBytes.ShouldBe(rawBytes);
        value.Base64Bytes.ShouldBe(encoded.Length);
        value.DecodedSha256.ShouldBe(Convert.ToHexString(SHA256.HashData(raw)));
        Encoding.UTF8.GetString(content.ShrunkJson)
            .ShouldBe(body.Replace(encoded, value.Marker, StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_value_that_carries_an_escaped_quote_is_still_read_as_one_value()
    {
        // The only long value a real body carries is base64, which has no
        // escapes. This sample exists because a scanner that ignored them would
        // end a value at the first quote inside it, and nothing else here would
        // notice.
        var value = new string('A', 5_000) + "\\\"" + new string('B', 100);
        var body = "{\"subject\":\"" + value + "\"}";

        MailSendBodyContent content = Read(body, 1, 4_097, 13);

        CapturedLargeValue single = content.LargeValues.ShouldHaveSingleItem();
        single.Base64Bytes.ShouldBe(value.Length);
        Encoding.UTF8.GetString(content.ShrunkJson)
            .ShouldBe(body.Replace(value, single.Marker, StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_over_the_inline_limit_that_is_not_base64_is_reported_as_undecodable()
    {
        var body = $$"""{"attachments":[{"content":"{{new string('!', 5_000)}}"}]}""";

        MailSendBodyContent content = Read(body, 4_096, 512);

        CapturedLargeValue value = content.LargeValues.ShouldHaveSingleItem();
        value.DecodedSuccessfully.ShouldBeFalse();
        value.Base64Bytes.ShouldBe(5_000);
    }

    [Fact]
    public void A_body_that_ends_inside_a_string_does_not_become_a_well_formed_document()
    {
        var truncated = """{"attachments":[{"content":"QUJD""";

        MailSendBodyContent content = Read(truncated, 5);

        // The opening quote goes in without its pair, so a parse of the shrunk
        // copy fails exactly where the connection died.
        Should.Throw<JsonException>(() => JsonDocument.Parse(content.ShrunkJson));
    }

    [Fact]
    public void The_digest_of_the_body_does_not_depend_on_how_the_bytes_arrived()
    {
        var body = $$"""{"attachments":[{"content":"{{Convert.ToBase64String(Pattern(9_001))}}"}]}""";

        MailSendBodyContent whole = Read(body, int.MaxValue);
        MailSendBodyContent shredded = Read(body, 1, 2, 3, 4_095, 4_096, 7);

        shredded.BodySha256.ShouldBe(whole.BodySha256);
        shredded.BodyBytes.ShouldBe(whole.BodyBytes);
        shredded.LargeValues[0].DecodedSha256.ShouldBe(whole.LargeValues[0].DecodedSha256);
    }

    private static byte[] Pattern(int length)
    {
        var value = new byte[length];
        for (var index = 0; index < length; index++)
        {
            value[index] = (byte)((index * 31) % 251);
        }

        return value;
    }

    /// <summary>Feeds the body in the given slice sizes, then whatever is left.</summary>
    private static MailSendBodyContent Read(string body, params int[] slices)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        using var filter = new MailSendBodyFilter(Marker, CaptureDepth.Decoded);
        var offset = 0;
        foreach (var slice in slices)
        {
            if (offset >= bytes.Length)
            {
                break;
            }

            var take = Math.Min(slice, bytes.Length - offset);
            filter.Append(bytes.AsSpan(offset, take));
            offset += take;
        }

        if (offset < bytes.Length)
        {
            filter.Append(bytes.AsSpan(offset));
        }

        return filter.Complete();
    }
}
