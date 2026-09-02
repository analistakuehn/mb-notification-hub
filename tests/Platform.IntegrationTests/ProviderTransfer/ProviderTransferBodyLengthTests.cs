using System.Text;
using System.Text.Json;
using NotificationHub.PerformanceTests.Gate;
using NotificationHub.PerformanceTests.ProviderTransfer;
using NotificationHub.PerformanceTests.Reporting;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.IntegrationTests.ProviderTransfer;

/// <summary>
/// How long the attachment field is, and who decides it. The answer has to be
/// the arithmetic of base64 and nothing else: if the length depended on the
/// content, a sender would choose it, and the ceiling of the whole message is
/// reachable with a fraction of the bytes the envelope admits.
/// </summary>
[Collection(ProviderTransferMeasurementCollectionDefinition.Name)]
public sealed class ProviderTransferBodyLengthTests
{
    private static ProviderTransferProfile Profile(long attachmentBytes, AttachmentContentShape shape)
        => new(
            ProviderTransferProfiles.Custom,
            attachmentBytes,
            1,
            shape,
            16 * 1_024,
            TimeSpan.Zero,
            2,
            1,
            DeclareContentLength: true);

    [Fact]
    public async Task The_adversarial_corpus_encodes_into_nothing_but_escapable_characters()
    {
        var source = new SyntheticAttachmentByteSource(
            3_072, "comprovante-0.pdf", "application/pdf", 1_024, TimeSpan.Zero,
            AttachmentContentShape.Escapable);
        var raw = new byte[3_072];
        await using (Stream stream = await source.OpenAsync(CancellationToken.None))
        {
            await stream.ReadExactlyAsync(raw, CancellationToken.None);
        }

        var encoded = Convert.ToBase64String(raw);

        encoded.Distinct().ShouldHaveSingleItem().ShouldBe('+');
        encoded.Length.ShouldBe((int)MailSendLimits.Base64Length(raw.Length));
    }

    [Fact]
    public async Task The_readable_corpus_encodes_into_no_escapable_character_at_all()
    {
        // Without this the adversarial corpus would prove nothing: the check it
        // arms is the same check the readable corpus passes either way.
        var source = new SyntheticAttachmentByteSource(
            3_072, "comprovante-0.pdf", "application/pdf", 1_024, TimeSpan.Zero);
        var raw = new byte[3_072];
        await using (Stream stream = await source.OpenAsync(CancellationToken.None))
        {
            await stream.ReadExactlyAsync(raw, CancellationToken.None);
        }

        Convert.ToBase64String(raw).Count(character => character is '+').ShouldBe(0);
    }

    [Fact]
    public void Handing_the_alphabet_to_the_json_encoder_makes_the_field_six_times_longer()
    {
        // The reason the composer never does it, measured rather than asserted.
        var raw = new byte[3_072];
        for (var index = 0; index < raw.Length; index += 3)
        {
            raw[index] = 0xFB;
            raw[index + 1] = 0xEF;
            raw[index + 2] = 0xBE;
        }

        var throughTheEncoder = JsonSerializer.SerializeToUtf8Bytes(Convert.ToBase64String(raw)).LongLength;
        var throughTheWriter = MailSendLimits.Base64Length(raw.Length) + 2;

        throughTheEncoder.ShouldBe(((throughTheWriter - 2) * 6) + 2);
        MailSendLimits.MaxMessageBytes.ShouldBeLessThan(
            MailSendLimits.Base64Length(ProviderTransferBudget.MaxTotalRawAttachmentBytes) * 6);
    }

    [Fact]
    public async Task The_body_measures_the_same_whether_the_content_is_readable_or_adversarial()
    {
        ProviderTransferOutcome readable = await ProviderTransferScenario.RunAsync(
            Profile(96 * 1_024, AttachmentContentShape.Readable),
            ProviderTransferArms.All,
            _ => { },
            CancellationToken.None);
        ProviderTransferOutcome adversarial = await ProviderTransferScenario.RunAsync(
            Profile(96 * 1_024, AttachmentContentShape.Escapable),
            ProviderTransferArms.All,
            _ => { },
            CancellationToken.None);

        adversarial.BodyBytes.ShouldBe(readable.BodyBytes);
        ProviderTransferInvariants.Violations(adversarial).ShouldBeEmpty();
        foreach (ProviderTransferArm arm in adversarial.Arms)
        {
            arm.CapturedBodyBytes.ShouldBe(adversarial.BodyBytes);
            arm.DeclaredContentLengthBytes.ShouldBe(arm.CapturedBodyBytes);
        }
    }

    [Fact]
    public async Task The_composed_body_carries_the_attachment_as_raw_base64_and_not_as_escapes()
    {
        var source = new SyntheticAttachmentByteSource(
            768, "comprovante-0.pdf", "application/pdf", 256, TimeSpan.Zero,
            AttachmentContentShape.Escapable);
        var raw = new byte[768];
        await using (Stream stream = await source.OpenAsync(CancellationToken.None))
        {
            await stream.ReadExactlyAsync(raw, CancellationToken.None);
        }

        var body = Encoding.UTF8.GetString(MailSendComposer.Serialize(
            MailSendEnvelope.Default.Compose(
            [
                new MailSendAttachment(
                    new AttachmentContent(raw), source.FileName, source.ContentType, "attachment"),
            ])));

        body.Contains(Convert.ToBase64String(raw), StringComparison.Ordinal).ShouldBeTrue(
            "o campo do anexo não saiu na forma codificada que a aritmética descreve");
        body.Contains("u002B", StringComparison.Ordinal).ShouldBeFalse(
            "o campo do anexo passou pelo codificador de escape");
    }
}
