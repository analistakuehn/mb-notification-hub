using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.PerformanceTests.ProviderTransfer;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.IntegrationTests.ProviderTransfer;

/// <summary>
/// The probe composes its own copy of the provider body, because it runs
/// outside the module that owns the adapter. A copy that drifted would make
/// every measurement answer about a message this hub never sends, so the two
/// shapes are compared byte for byte here.
/// </summary>
public sealed class MailSendEnvelopeShapeTests
{
    [Fact]
    public void The_probe_envelope_serializes_exactly_like_the_request_the_email_adapter_builds()
    {
        var envelope = new MailSendEnvelope(
            "person@example.com",
            "no-reply@example.com",
            "Notification Hub",
            "Confirme sua operação",
            "Olá",
            "<p>Olá</p>",
            true);
        SendGridMailRequest fromAdapter = SendGridChannelProvider.BuildRequest(
            new EmailDeliveryTarget("person@example.com"),
            new EmailMessage("Confirme sua operação", "Aguardando confirmação", "<p>Olá</p>", "Olá"),
            new SendGridOptions
            {
                SenderEmail = "no-reply@example.com",
                SenderName = "Notification Hub",
                SandboxMode = true,
            });

        var adapterBody = JsonSerializer.SerializeToUtf8Bytes(fromAdapter);
        var probeBody = MailSendComposer.Serialize(envelope.Compose(null));

        Encoding.UTF8.GetString(probeBody).ShouldBe(Encoding.UTF8.GetString(adapterBody));
    }

    [Fact]
    public void An_attachment_enters_the_body_as_content_filename_type_and_disposition()
    {
        MailSendEnvelope envelope = MailSendEnvelope.Default;

        var body = MailSendComposer.Serialize(envelope.Compose(
        [
            new MailSendAttachment(
                new AttachmentContent("ABC"u8.ToArray()),
                "comprovante.pdf",
                "application/pdf",
                "attachment"),
        ]));

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement attachment = document.RootElement.GetProperty("attachments")[0];
        attachment.GetProperty("content").GetString().ShouldBe("QUJD");
        attachment.GetProperty("filename").GetString().ShouldBe("comprovante.pdf");
        attachment.GetProperty("type").GetString().ShouldBe("application/pdf");
        attachment.GetProperty("disposition").GetString().ShouldBe("attachment");
        attachment.TryGetProperty("content_id", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(1_023)]
    [InlineData(1_024)]
    [InlineData(65_536)]
    [InlineData(1_048_576)]
    public void The_base64_size_rule_matches_what_the_encoder_produces(int rawBytes)
        => MailSendLimits.Base64Length(rawBytes)
            .ShouldBe(Convert.ToBase64String(new byte[rawBytes]).Length);

    [Fact]
    public async Task The_composed_layout_reassembles_into_the_body_the_serializer_produces()
    {
        var source = new SyntheticAttachmentByteSource(
            96, "comprovante-0.pdf", "application/pdf", 32, TimeSpan.Zero);
        var raw = new byte[96];
        await using (Stream stream = await source.OpenAsync(CancellationToken.None))
        {
            await stream.ReadExactlyAsync(raw, CancellationToken.None);
        }

        MailSendBodyLayout layout = MailSendComposer.Layout(
            MailSendEnvelope.Default, [source], "probe-layout-check");
        var assembled = new List<byte>();
        assembled.AddRange(layout.Segments[0]);
        assembled.AddRange(Encoding.UTF8.GetBytes(Convert.ToBase64String(raw)));
        assembled.AddRange(layout.Segments[1]);

        var serialized = MailSendComposer.Serialize(MailSendEnvelope.Default.Compose(
        [
            new MailSendAttachment(
                new AttachmentContent(raw), source.FileName, source.ContentType, "attachment"),
        ]));
        assembled.ToArray().ShouldBe(serialized);
        layout.TotalBytes.ShouldBe(serialized.LongLength);
    }

    [Fact]
    public async Task A_message_whose_base64_crosses_the_provider_ceiling_is_refused_before_it_is_measured()
    {
        // Twenty-three megabytes of attachment expand to more than the thirty
        // the provider accepts for the whole message.
        var profile = new ProviderTransferProfile(
            ProviderTransferProfiles.Custom,
            23_000_000,
            1,
            AttachmentContentShape.Readable,
            64 * 1_024,
            TimeSpan.Zero,
            1,
            1,
            DeclareContentLength: true);

        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => ProviderTransferScenario.RunAsync(
                profile, ProviderTransferArms.All, _ => { }, CancellationToken.None));

        refusal.Message.ShouldContain("30,000,000");
    }

    [Fact]
    public void The_largest_attachment_that_still_fits_is_bounded_by_the_base64_expansion()
    {
        // The ceiling is on the message, and base64 is what spends it: three
        // raw bytes become four, so the raw budget is three quarters of it.
        var raw = MailSendLimits.MaxMessageBytes * 3 / 4;

        MailSendLimits.Base64Length(raw).ShouldBeGreaterThan(MailSendLimits.MaxMessageBytes - 4);
        MailSendLimits.Base64Length(raw - 1_024).ShouldBeLessThan(MailSendLimits.MaxMessageBytes);
    }
}
