using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.UnitTests.Dispatch;

/// <summary>
/// What the body writer puts on the connection.
/// <para>
/// The writer is the half of the composition that holds nothing: it opens each
/// attachment when the connection is ready for it, encodes it in blocks and
/// closes it on the way out. The measurements here are taken on the bytes it
/// wrote, so a body that disagreed with the length declared before the call
/// shows up as the difference it is, rather than as a broken connection much
/// later.
/// </para>
/// </summary>
public sealed class SendGridMailContentTests
{
    private static readonly EmailDeliveryTarget Target = new("person@example.com");

    private static readonly EmailMessage Message = new(
        "Confirme sua operação", "Aguardando confirmação", "<p>Olá</p>", "Olá");

    private static readonly SendGridOptions Config = new()
    {
        SenderEmail = "no-reply@example.com",
        SandboxMode = true,
    };

    /// <summary>
    /// The whole set reaches the wire, in order, with the bytes it was
    /// released over, and the body measures exactly what was declared before
    /// any of it moved.
    /// <para>
    /// The block sizes are the point of the theory: the encoder works in
    /// quartets and a reading that stops mid quartet has to carry bytes to the
    /// next one, so a writer that dropped or repeated a carry produces content
    /// that no longer decodes to what was read. One block that hands over a
    /// single byte at a time is the harshest form of that.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public async Task The_whole_set_reaches_the_wire_in_order_and_measures_what_was_declared(
        int chunkBytes)
    {
        byte[][] contents = [Bytes(1_000), Bytes(1), Bytes(4_099)];
        StubAcceptedAttachmentContent custody = Custody(contents, chunkBytes);
        SendGridMailBody body = Composed(contents);

        var written = await WriteAsync(body, custody);

        written.LongLength.ShouldBe(body.DeclaredLength);
        custody.Opened.ShouldBe(["aci_0", "aci_1", "aci_2"]);
        using JsonDocument document = JsonDocument.Parse(written);
        JsonElement fields = document.RootElement.GetProperty("attachments");
        for (var index = 0; index < contents.Length; index++)
        {
            var received = Convert.FromBase64String(
                fields[index].GetProperty("content").GetString()!);
            Digest(received).ShouldBe(Digest(contents[index]));
            fields[index].GetProperty("filename").GetString()
                .ShouldBe("comprovante-" + index.ToString(CultureInfo.InvariantCulture) + ".pdf");
        }
    }

    /// <summary>
    /// Content chosen by the sender does not lengthen the message. The base64
    /// goes onto the connection in the alphabet the encoder produces and
    /// through no escape encoder at all, so the body measures the arithmetic
    /// and nothing else.
    /// <para>
    /// The two arms are the whole point. With readable bytes, a writer that
    /// emitted the field as an ordinary JSON string measures exactly the same
    /// as this one; with the pattern whose base64 is entirely escapable, the
    /// same writer produces six bytes for every one and the body stops
    /// matching its declared length. A case without the adversarial arm would
    /// pass under either writer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Content_chosen_by_the_sender_does_not_lengthen_the_message()
    {
        byte[][] adversarial = [Adversarial(30_000)];
        byte[][] readable = [Bytes(30_000)];
        SendGridMailBody hostile = Composed(adversarial);
        SendGridMailBody ordinary = Composed(readable);

        var hostileBody = await WriteAsync(hostile, Custody(adversarial, int.MaxValue));
        var ordinaryBody = await WriteAsync(ordinary, Custody(readable, int.MaxValue));

        hostileBody.LongLength.ShouldBe(hostile.DeclaredLength);
        hostileBody.LongLength.ShouldBe(ordinaryBody.LongLength);
        hostile.Attachments[0].EncodedLength.ShouldBe(40_000);
    }

    /// <summary>
    /// A send that carries no attachment opens nothing. It is the cheapest way
    /// this composition can be wrong: a writer that opened a slot it does not
    /// have would reach custody on every message this hub sends.
    /// </summary>
    [Fact]
    public async Task A_send_that_carries_no_attachment_opens_nothing()
    {
        var custody = new StubAcceptedAttachmentContent();
        SendGridMailBody body = SendGridMailBody
            .Compose(Target, Message, Config, null, null)
            .Body
            .ShouldNotBeNull();

        var written = await WriteAsync(body, custody);

        custody.Opened.ShouldBeEmpty();
        written.LongLength.ShouldBe(body.DeclaredLength);
    }

    /// <summary>
    /// A custody that hands nothing over stops the body, and says so in its
    /// own words rather than as a broken connection. The two are different
    /// systems, and an operator told the provider was unreachable would go
    /// looking in the wrong one.
    /// </summary>
    [Fact]
    public async Task A_custody_that_hands_nothing_over_stops_the_body_and_names_itself()
    {
        byte[][] contents = [Bytes(64)];
        SendGridMailBody body = Composed(contents);
        using var content = new SendGridMailContent(body, new StubAcceptedAttachmentContent());

        await Should.ThrowAsync<HttpRequestException>(
            async () => await WriteAsync(content));

        content.Interrupted.ShouldBe(SendGridMailContent.ContentUnavailable);
    }

    /// <summary>
    /// A source of another size than the one the release was granted over
    /// stops the body, whichever way it differs.
    /// <para>
    /// The declared length was computed from that value, so a source that runs
    /// short leaves the provider waiting for bytes that never come, and one
    /// that keeps going puts on the wire the very bytes that make the body
    /// disagree with what the provider was told to expect. The second is
    /// stopped before those bytes are written, which is what keeps a message
    /// from ever carrying content nobody measured.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(-3)]
    [InlineData(3)]
    public async Task A_source_of_another_size_than_the_release_stops_the_body(int difference)
    {
        byte[][] declared = [Bytes(300)];
        SendGridMailBody body = Composed(declared);
        byte[][] delivered = [Bytes(300 + difference)];
        using var content = new SendGridMailContent(body, Custody(delivered, int.MaxValue));
        using var destination = new MemoryStream();

        await Should.ThrowAsync<HttpRequestException>(
            async () => await content.CopyToAsync(destination));

        content.Interrupted.ShouldBe(SendGridMailContent.ContentLengthChanged);

        // And the extra bytes never reached the wire. Measured on the field
        // rather than on the whole body: a body cut short by the refusal is
        // shorter than the declared length whatever it wrote, so the total
        // says nothing, while the content written past the length the release
        // was granted over says exactly what it is.
        var contentWritten = destination.Length - body.Segments[0].Length;
        contentWritten.ShouldBeLessThanOrEqualTo(body.Attachments[0].EncodedLength);
    }

    /// <summary>
    /// The witness names every member the body wrote, in order, with the
    /// handle it was written under, the number of raw bytes that went out and
    /// the digest of exactly those bytes.
    /// <para>
    /// The digest is compared against one this test takes over the array it
    /// planted, which is the whole reason the comparison can fail: a witness
    /// that reported the length it was told to expect, or a digest derived
    /// from anything but the bytes read, disagrees here. The block sizes sweep
    /// for the same reason as above: the measurement is fed the spans the
    /// encoder is handed, so a carry the writer forgot to feed it, or fed
    /// twice, changes the digest and nothing else.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public async Task The_witness_names_each_member_with_the_digest_of_the_bytes_that_went_out(
        int chunkBytes)
    {
        byte[][] contents = [Bytes(1_000), Bytes(1), Bytes(4_099)];
        SendGridMailBody body = Composed(contents);
        using var content = new SendGridMailContent(body, Custody(contents, chunkBytes));

        await WriteAsync(content);

        content.Submitted.Count.ShouldBe(contents.Length);
        for (var index = 0; index < contents.Length; index++)
        {
            SubmittedAttachmentBytes member = content.Submitted[index];
            member.ContentIdentity.ShouldBe(
                "aci_" + index.ToString(CultureInfo.InvariantCulture));
            member.Length.ShouldBe(contents[index].LongLength);
            Convert.ToHexString(member.Digest.Span)
                .ShouldBe(Digest(contents[index]));
        }
    }

    /// <summary>
    /// A member the body could not finish is left out of the witness. A digest
    /// over a prefix would be a measurement of something nobody sent, and it
    /// would be indistinguishable, to whoever settles it later, from a
    /// divergence of the bytes themselves.
    /// <para>
    /// The set carries two members and the second one is the one that breaks,
    /// so the count that comes out is one rather than zero. A witness that
    /// simply never recorded anything would satisfy an assertion that only
    /// asked for the absence of the broken member.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_member_the_body_could_not_finish_is_left_out_of_the_witness()
    {
        byte[][] declared = [Bytes(300), Bytes(300)];
        SendGridMailBody body = Composed(declared);
        byte[][] delivered = [declared[0], Bytes(297)];
        using var content = new SendGridMailContent(body, Custody(delivered, int.MaxValue));

        await Should.ThrowAsync<HttpRequestException>(async () => await WriteAsync(content));

        content.Interrupted.ShouldBe(SendGridMailContent.ContentLengthChanged);
        content.Submitted.Count.ShouldBe(1);
        content.Submitted[0].ContentIdentity.ShouldBe("aci_0");
        Convert.ToHexString(content.Submitted[0].Digest.Span).ShouldBe(Digest(declared[0]));
    }

    /// <summary>
    /// A body written twice witnesses one set and not two. The transport may
    /// write a request body again, and a witness that accumulated would hand
    /// the module that settles it a submission with every member listed once
    /// per attempt, which describes a message nobody sent.
    /// </summary>
    [Fact]
    public async Task A_body_written_twice_witnesses_the_set_that_left_and_not_both_passes()
    {
        byte[][] contents = [Bytes(1_000), Bytes(2_048)];
        SendGridMailBody body = Composed(contents);
        using var content = new SendGridMailContent(body, Custody(contents, 512));

        await WriteAsync(content);
        await WriteAsync(content);

        content.Submitted.Count.ShouldBe(contents.Length);
        content.Submitted.Select(member => member.ContentIdentity)
            .ShouldBe(["aci_0", "aci_1"]);
        Convert.ToHexString(content.Submitted[1].Digest.Span).ShouldBe(Digest(contents[1]));
    }

    private static async Task<byte[]> WriteAsync(
        SendGridMailBody body,
        IAcceptedAttachmentContent custody)
    {
        using var content = new SendGridMailContent(body, custody);
        return await WriteAsync(content);
    }

    private static async Task<byte[]> WriteAsync(SendGridMailContent content)
    {
        using var destination = new MemoryStream();
        await content.CopyToAsync(destination);
        return destination.ToArray();
    }

    private static SendGridMailBody Composed(byte[][] contents)
        => SendGridMailBody
            .Compose(Target, Message, Config, null, Set(contents))
            .Body
            .ShouldNotBeNull();

    private static StubAcceptedAttachmentContent Custody(byte[][] contents, int chunkBytes)
    {
        var custody = new StubAcceptedAttachmentContent { ChunkBytes = chunkBytes };
        for (var index = 0; index < contents.Length; index++)
        {
            custody.Plant("aci_" + index.ToString(CultureInfo.InvariantCulture), contents[index]);
        }

        return custody;
    }

    private static AcceptedAttachmentSet Set(byte[][] contents)
        => AcceptedAttachmentSet.Of(contents.Select((content, index) => new AcceptedAttachment
        {
            Reference = "att_" + index.ToString(CultureInfo.InvariantCulture),
            ContentIdentity = "aci_" + index.ToString(CultureInfo.InvariantCulture),
            Name = "comprovante-" + index.ToString(CultureInfo.InvariantCulture) + ".pdf",
            MediaType = "application/pdf",
            Length = content.Length,
        }));

    private static string Digest(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static byte[] Bytes(int length) => RandomNumberGenerator.GetBytes(length);

    /// <summary>
    /// Content whose base64 is made of nothing but the character the default
    /// encoder escapes, six bytes at a time.
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
