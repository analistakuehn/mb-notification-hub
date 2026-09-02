using System.Runtime;
using NotificationHub.PerformanceTests.Gate;
using NotificationHub.PerformanceTests.ProviderTransfer;
using NotificationHub.PerformanceTests.Reporting;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.IntegrationTests.ProviderTransfer;

/// <summary>
/// Whether the three transfer methods do the same job. Nothing here reads a
/// cost: a comparison whose arms sent different messages is not a slow
/// comparison, it is no comparison at all, and this is where that is settled.
/// </summary>
[Collection(ProviderTransferMeasurementCollectionDefinition.Name)]
public sealed class ProviderTransferEquivalenceTests
{
    private static ProviderTransferProfile Profile(
        long attachmentBytes,
        int attachments = 1,
        bool declareContentLength = true,
        AttachmentContentShape shape = AttachmentContentShape.Readable)
        => new(
            ProviderTransferProfiles.Custom,
            attachmentBytes,
            attachments,
            shape,
            16 * 1_024,
            TimeSpan.Zero,
            2,
            1,
            declareContentLength);

    [Fact]
    public async Task The_three_transfer_methods_deliver_the_same_bytes_to_the_provider()
    {
        ProviderTransferOutcome outcome = await ProviderTransferScenario.RunAsync(
            Profile(256 * 1_024), ProviderTransferArms.All, _ => { }, CancellationToken.None);

        outcome.Arms.Select(arm => arm.CapturedBodySha256)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(1, "os braços entregaram corpos diferentes ao provedor");
        outcome.ArmsAgreeOnBody.ShouldBeTrue();
        ProviderTransferInvariants.Violations(outcome).ShouldBeEmpty();
        outcome.Arms.Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_provider_reads_back_the_content_of_every_attachment_in_the_order_it_was_sent()
    {
        ProviderTransferOutcome outcome = await ProviderTransferScenario.RunAsync(
            Profile(48 * 1_024, attachments: 3), ProviderTransferArms.All, _ => { }, CancellationToken.None);

        foreach (ProviderTransferArm arm in outcome.Arms)
        {
            arm.Attachments.Count.ShouldBe(3);
            for (var order = 0; order < 3; order++)
            {
                ProviderTransferAttachmentCheck check = arm.Attachments[order];

                // Every source is seeded from its own file name, so an arm that
                // shuffled the attachments would fail the digest here and not
                // only the name.
                check.DigestMatchesSource.ShouldBeTrue(
                    $"o braço {arm.ArmId} entregou o anexo {order} com outro conteúdo");
                check.MetadataMatches.ShouldBeTrue(
                    $"o braço {arm.ArmId} entregou o anexo {order} com outro nome, tipo ou ordem");
                check.FileName.ShouldBe($"comprovante-{order}.pdf");
                check.DecodedBytes.ShouldBe(check.SourceBytes);
                check.Base64Bytes.ShouldBe(MailSendLimits.Base64Length(check.SourceBytes));
            }
        }
    }

    [Fact]
    public async Task An_attachment_small_enough_to_stay_inline_is_read_back_like_a_streamed_one()
    {
        // A kibibyte becomes 1.368 base64 bytes, which is under the limit the
        // double keeps values inline at, so this run exercises the decoding
        // path the big attachments never take.
        ProviderTransferOutcome inline = await ProviderTransferScenario.RunAsync(
            Profile(1_024), ProviderTransferArms.All, _ => { }, CancellationToken.None);

        MailSendLimits.Base64Length(1_024).ShouldBeLessThan(4_096);
        ProviderTransferInvariants.Violations(inline).ShouldBeEmpty();
        inline.Arms.ShouldAllBe(arm => arm.Attachments[0].DigestMatchesSource);
    }

    [Fact]
    public async Task A_chunked_body_carries_the_same_bytes_as_one_that_declares_its_length()
    {
        ProviderTransferOutcome declared = await ProviderTransferScenario.RunAsync(
            Profile(128 * 1_024), ProviderTransferArms.All, _ => { }, CancellationToken.None);
        ProviderTransferOutcome chunked = await ProviderTransferScenario.RunAsync(
            Profile(128 * 1_024, declareContentLength: false),
            ProviderTransferArms.All,
            _ => { },
            CancellationToken.None);

        chunked.Arms[0].CapturedBodySha256.ShouldBe(declared.Arms[0].CapturedBodySha256);
        chunked.Arms.ShouldAllBe(arm => arm.ChunkedObserved);
        declared.Arms.ShouldAllBe(arm => !arm.ChunkedObserved);
        ProviderTransferInvariants.Violations(chunked).ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_operation_of_every_arm_reaches_the_provider_and_is_accepted()
    {
        var profile = new ProviderTransferProfile(
            ProviderTransferProfiles.Custom,
            64 * 1_024,
            1,
            AttachmentContentShape.Readable,
            16 * 1_024,
            TimeSpan.Zero,
            6,
            3,
            DeclareContentLength: true);

        ProviderTransferOutcome outcome = await ProviderTransferScenario.RunAsync(
            profile, ProviderTransferArms.All, _ => { }, CancellationToken.None);

        foreach (ProviderTransferArm arm in outcome.Arms)
        {
            arm.ProviderCalls.ShouldBe(6);
            arm.AcceptedCalls.ShouldBe(6);
            arm.DistinctCapturedDigests.ShouldBe(1);
            arm.PeakConcurrency.ShouldBeGreaterThan(0);
            arm.PeakConcurrency.ShouldBeLessThanOrEqualTo(3);
            arm.TemporaryFilesRemaining.ShouldBe(0);
            arm.TemporaryRootRemoved.ShouldBeTrue();
            arm.OpenSourceStreams.ShouldBe(0);
        }
    }

    [Fact]
    public async Task A_run_under_the_workstation_collector_is_reported_as_such()
    {
        // The probe process declares the server collector; a test host does not
        // have to. What matters is that the report says which one ran, because
        // a measurement whose collector is unknown is not comparable to any
        // other.
        ProviderTransferOutcome outcome = await ProviderTransferScenario.RunAsync(
            Profile(16 * 1_024), ProviderTransferArms.All, _ => { }, CancellationToken.None);

        outcome.ServerGarbageCollection.ShouldBe(GCSettings.IsServerGC);
        outcome.GarbageCollectorLatencyMode.ShouldNotBeNullOrWhiteSpace();
    }
}
