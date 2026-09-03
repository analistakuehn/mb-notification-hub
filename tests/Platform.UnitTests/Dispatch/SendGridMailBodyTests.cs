using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.UnitTests.Dispatch;

/// <summary>
/// How one Mail Send body is assembled, and what it costs.
/// <para>
/// The composition is the one place a body is put together, so it answers two
/// questions that used to have no single owner: which bytes leave, and how
/// many of them. Everything here is measured on the composed body itself,
/// never on a description of it.
/// </para>
/// </summary>
public sealed class SendGridMailBodyTests
{
    private static readonly EmailDeliveryTarget Target = new("person@example.com");

    private static readonly EmailMessage Message = new(
        "Confirme sua operação", "Aguardando confirmação", "<p>Olá</p>", "Olá");

    /// <summary>
    /// The encoder the body is deliberately not serialized with. It escapes
    /// the character the adversarial pattern is made of, which is what makes
    /// it the instrument that tells the two write calls apart.
    /// </summary>
    private static readonly JsonSerializerOptions Escaping = new();

    private static readonly SendGridOptions Config = new()
    {
        SenderEmail = "no-reply@example.com",
        SenderName = "Notification Hub",
        SandboxMode = true,
    };

    /// <summary>
    /// A send with no attachment is the same composition with no slots in it,
    /// and its bytes are the ones the serializer produces on its own. Two
    /// assemblies of the same message would be two shapes free to drift, and
    /// the drift would land on what this hub actually sends.
    /// </summary>
    [Fact]
    public void A_send_with_no_attachment_composes_the_bytes_the_serializer_produces_on_its_own()
    {
        SendGridMailBody body = Composed(null);

        var expected = JsonSerializer.SerializeToUtf8Bytes(
            SendGridChannelProvider.BuildRequest(Target, Message, Config),
            SendGridChannelProvider.BodySerialization);

        body.Attachments.ShouldBeEmpty();
        body.Segments.ShouldHaveSingleItem().ShouldBe(expected);
        body.DeclaredLength.ShouldBe(expected.LongLength);
    }

    /// <summary>
    /// Every member of the set becomes a field of the one body, in the order
    /// it was accepted in, carrying the name and the media type it was
    /// released under and the disposition that makes the provider deliver it
    /// as a file.
    /// </summary>
    [Fact]
    public void Every_member_becomes_a_field_of_the_one_body_in_the_order_it_was_accepted_in()
    {
        byte[][] contents = [Readable(300), Readable(31), Readable(1_024)];
        SendGridMailBody body = Composed(SetOf(contents));

        using JsonDocument document = JsonDocument.Parse(Assemble(body, contents));
        JsonElement fields = document.RootElement.GetProperty("attachments");

        fields.GetArrayLength().ShouldBe(3);
        for (var index = 0; index < contents.Length; index++)
        {
            JsonElement field = fields[index];
            field.GetProperty("filename").GetString().ShouldBe(Name(index));
            field.GetProperty("type").GetString().ShouldBe(MediaType(index));
            field.GetProperty("disposition").GetString().ShouldBe("attachment");
            Convert.FromBase64String(field.GetProperty("content").GetString()!)
                .ShouldBe(contents[index]);
        }
    }

    /// <summary>
    /// The declared length is known before a byte moves, and it is the length
    /// the body actually measures. It is the envelope plus four bytes of
    /// base64 for every three bytes of content, which is the whole reason the
    /// envelope of a product decision and the ceiling of this provider are not
    /// the same number.
    /// </summary>
    [Fact]
    public void The_declared_length_is_the_envelope_plus_the_base64_of_every_member()
    {
        byte[][] contents = [Readable(3_001), Readable(7)];
        SendGridMailBody body = Composed(SetOf(contents));

        var envelope = body.Segments.Sum(segment => (long)segment.Length);
        var encoded = contents.Sum(content => SendGridMailLimits.Base64Length(content.Length));

        body.DeclaredLength.ShouldBe(envelope + encoded);
        body.DeclaredLength.ShouldBe(Assemble(body, contents).LongLength);
        body.Attachments.Select(slot => slot.RawLength)
            .ShouldBe([3_001L, 7L]);
        body.Attachments.Select(slot => slot.EncodedLength)
            .ShouldBe([4_004L, 12L]);
    }

    /// <summary>
    /// The largest set that fits is bounded by the base64 expansion and not by
    /// the raw bytes: three raw bytes cost four of body, so the raw budget is
    /// three quarters of what is left of the ceiling after the envelope.
    /// <para>
    /// The boundary is derived from the composition itself rather than written
    /// down here. A number copied into a test agrees with whatever the code
    /// does the day either of them moves; a boundary measured from the
    /// envelope this very composition produces cannot.
    /// </para>
    /// </summary>
    [Fact]
    public void The_largest_set_that_fits_is_bounded_by_the_base64_expansion()
    {
        // What one field costs beside its content: the name, the media type,
        // the disposition and the punctuation around them.
        var overhead = Composed(Declaring(3)).DeclaredLength - SendGridMailLimits.Base64Length(3);
        var largest = (SendGridMailLimits.MaxBodyBytes - overhead) / 4 * 3;

        SendGridMailBody fitting = Composed(Declaring(largest));
        fitting.DeclaredLength.ShouldBeLessThanOrEqualTo(SendGridMailLimits.MaxBodyBytes);
        fitting.DeclaredLength.ShouldBeGreaterThan(SendGridMailLimits.MaxBodyBytes - 4);

        // One quantum of base64 more, which is three raw bytes, and the same
        // message no longer fits.
        SendGridMailBody.Compose(Target, Message, Config, null, Declaring(largest + 3))
            .Body.ShouldBeNull();
    }

    /// <summary>
    /// The refusal is about the whole set and not about any one member: two
    /// members that each fit on their own do not fit together, which is the
    /// case a per member ceiling would miss.
    /// </summary>
    [Fact]
    public void A_set_whose_members_fit_one_by_one_is_still_refused_when_they_do_not_fit_together()
    {
        var half = SendGridMailLimits.MaxBodyBytes / 2;

        Composed(Declaring(half)).DeclaredLength
            .ShouldBeLessThan(SendGridMailLimits.MaxBodyBytes);
        SendGridMailBody.Compose(Target, Message, Config, null, Declaring(half, half))
            .Body.ShouldBeNull();
    }

    /// <summary>
    /// A length no message could ever carry is refused like any other, and it
    /// is refused by arithmetic that cannot overflow: the raw length is
    /// compared against what is left before it is ever multiplied, so a value
    /// whose base64 does not fit in the type never reaches the multiplication.
    /// <para>
    /// The second length is the one that matters, and it was found by
    /// measurement rather than chosen for size. Multiplied by four thirds it
    /// lands exactly on the sign bit, so a measurement that multiplied first
    /// would compare a negative number against what is left, conclude that it
    /// fits, and clear for sending a message of six exabytes. The largest
    /// value of the type does not expose that: it wraps back into a large
    /// positive number and is refused by the next comparison anyway.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(6_917_529_027_641_081_854)]
    public void A_member_larger_than_any_message_is_refused_and_never_multiplied(long length)
        => SendGridMailBody.Compose(Target, Message, Config, null, Declaring(length))
            .Body.ShouldBeNull();

    /// <summary>
    /// The attachment field costs the arithmetic of base64 and nothing else,
    /// under the encoder that escapes as well as under the one that does not.
    /// <para>
    /// That is a statement about which write call emits the field, not about
    /// which encoder the body is serialized with. Written as an ordinary
    /// string value, base64 goes through the escape encoder character by
    /// character, and content chosen by a sender then chooses how long the
    /// field becomes: the pattern below is the one whose base64 is entirely
    /// escapable, and under the default encoder it costs six bytes for every
    /// one. Under four megabytes of it reach the ceiling of a whole message.
    /// </para>
    /// <para>
    /// The readable arm beside it is what makes the adversarial one worth
    /// running: with readable bytes both write calls measure the same, so a
    /// case without this content would pass with either of them and prove
    /// nothing at all.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(3_000)]
    [InlineData(300_000)]
    public void The_attachment_field_costs_only_the_arithmetic_of_base64_under_any_encoder(int length)
    {
        var expected = SendGridMailLimits.Base64Length(length) + 2;
        var adversarial = new SendGridAttachmentContent(Adversarial(length));
        var readable = new SendGridAttachmentContent(Readable(length));

        // The encoder that escapes, which is the one the field must not be
        // sensitive to, and then the one the body is actually serialized with.
        Measure(adversarial, Escaping).ShouldBe(expected);
        Measure(readable, Escaping).ShouldBe(expected);
        Measure(adversarial, SendGridChannelProvider.BodySerialization).ShouldBe(expected);
        Measure(readable, SendGridChannelProvider.BodySerialization).ShouldBe(expected);
    }

    /// <summary>
    /// Adversarial content is only adversarial because an ordinary string
    /// write would expand it. The measurement is here so the case above cannot
    /// quietly become a comparison of two identical things: the day this
    /// pattern stops expanding, this fails and says so.
    /// </summary>
    [Fact]
    public void The_adversarial_pattern_is_what_an_ordinary_string_write_would_expand()
    {
        var text = Convert.ToBase64String(Adversarial(3_000));

        var escaped = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text, Escaping));

        escaped.ShouldBe((text.Length * 6) + 2);
    }

    /// <summary>
    /// The cut is refused unless the field appears exactly once. Absent says
    /// the write call did not put the field on the wire in the form the cut
    /// assumes; twice says something else in the message carries the same
    /// bytes, and cutting on either occurrence would assemble a body in an
    /// order nobody composed.
    /// </summary>
    [Fact]
    public void The_cut_refuses_a_field_that_is_absent_and_one_that_appears_twice()
    {
        var needle = "QUJD"u8.ToArray();

        Should.Throw<InvalidOperationException>(
            () => SendGridMailBody.Cut("{\"content\":\"other\"}"u8.ToArray(), [needle]));
        Should.Throw<InvalidOperationException>(
            () => SendGridMailBody.Cut("{\"a\":\"QUJD\",\"b\":\"QUJD\"}"u8.ToArray(), [needle]));

        SendGridMailBody.Cut("{\"a\":\"QUJD\"}"u8.ToArray(), [needle])
            .Select(Encoding.UTF8.GetString)
            .ShouldBe(["{\"a\":\"", "\"}"]);
    }

    private static SendGridMailBody Composed(AcceptedAttachmentSet? attachments)
        => SendGridMailBody.Compose(Target, Message, Config, null, attachments)
            .Body
            .ShouldNotBeNull();

    private static long Measure(SendGridAttachmentContent content, JsonSerializerOptions options)
        => JsonSerializer.SerializeToUtf8Bytes(content, options).LongLength;

    /// <summary>
    /// The body as it reaches the wire: the literal parts with the base64 of
    /// each content between them, which is exactly what the writer emits.
    /// </summary>
    private static byte[] Assemble(SendGridMailBody body, byte[][] contents)
    {
        var assembled = new List<byte>();
        for (var index = 0; index < contents.Length; index++)
        {
            assembled.AddRange(body.Segments[index]);
            assembled.AddRange(Encoding.UTF8.GetBytes(Convert.ToBase64String(contents[index])));
        }

        assembled.AddRange(body.Segments[^1]);
        return [.. assembled];
    }

    private static AcceptedAttachmentSet SetOf(byte[][] contents)
        => AcceptedAttachmentSet.Of(contents.Select((content, index) => Item(index, content.Length)));

    /// <summary>
    /// A set that declares lengths and carries no bytes. The measurement reads
    /// the lengths the release was granted over and never opens anything, so a
    /// set of twenty megabytes costs a test nothing to state.
    /// </summary>
    private static AcceptedAttachmentSet Declaring(params long[] lengths)
        => AcceptedAttachmentSet.Of(lengths.Select((length, index) => Item(index, length)));

    private static AcceptedAttachment Item(int index, long length) => new()
    {
        Reference = "att_" + index.ToString(CultureInfo.InvariantCulture),
        ContentIdentity = "aci_" + index.ToString(CultureInfo.InvariantCulture),
        Name = Name(index),
        MediaType = MediaType(index),
        Length = length,
    };

    private static string Name(int index)
        => "comprovante-" + index.ToString(CultureInfo.InvariantCulture) + ".pdf";

    private static string MediaType(int index) => index % 2 == 0 ? "application/pdf" : "image/png";

    /// <summary>
    /// Content whose base64 carries no character any encoder escapes. It is
    /// the corpus under which the correct write call and the expansive one
    /// measure the same, which is why no case here uses it alone.
    /// </summary>
    private static byte[] Readable(int length) => RandomNumberGenerator.GetBytes(length);

    /// <summary>
    /// Content whose base64 is made of nothing but the character the default
    /// encoder escapes. Three bytes of this pattern become four plus signs,
    /// and every one of them costs six bytes when written as an ordinary
    /// string.
    /// </summary>
    private static byte[] Adversarial(int length)
    {
        var content = new byte[length];
        for (var index = 0; index + 2 < content.Length; index += 3)
        {
            content[index] = 0xFB;
            content[index + 1] = 0xEF;
            content[index + 2] = 0xBE;
        }

        return content;
    }
}
