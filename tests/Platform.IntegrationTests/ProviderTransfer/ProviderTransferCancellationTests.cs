using System.Globalization;
using NotificationHub.PerformanceTests.ProviderTransfer;

namespace NotificationHub.IntegrationTests.ProviderTransfer;

/// <summary>
/// What an interrupted transfer leaves behind. The injection points are the
/// three places the work happens, and the answer has to be the same at all of
/// them: no temporary file, no open read, and nothing the provider counted as
/// a message.
/// </summary>
[Collection(ProviderTransferMeasurementCollectionDefinition.Name)]
public sealed class ProviderTransferCancellationTests
{
    private const long AttachmentBytes = 256 * 1_024;

    // The stage travels as its name because the enum belongs to the probe and a
    // public test signature cannot name an internal type. nameof keeps the
    // reference compile-checked instead of turning it into a literal that rots.
    [Theory]
    [InlineData(ProviderTransferArms.BufferArm, nameof(TransferStage.SourceRead))]
    [InlineData(ProviderTransferArms.BufferArm, nameof(TransferStage.Encode))]
    [InlineData(ProviderTransferArms.BufferArm, nameof(TransferStage.HttpWrite))]
    [InlineData(ProviderTransferArms.StreamingArm, nameof(TransferStage.SourceRead))]
    [InlineData(ProviderTransferArms.StreamingArm, nameof(TransferStage.Encode))]
    [InlineData(ProviderTransferArms.StreamingArm, nameof(TransferStage.HttpWrite))]
    [InlineData(ProviderTransferArms.SpoolArm, nameof(TransferStage.SourceRead))]
    [InlineData(ProviderTransferArms.SpoolArm, nameof(TransferStage.Encode))]
    [InlineData(ProviderTransferArms.SpoolArm, nameof(TransferStage.HttpWrite))]
    public async Task Cancelling_a_transfer_at_any_stage_leaves_no_temporary_file_and_no_open_read(
        string armId,
        string stageName)
    {
        TransferStage stage = Enum.Parse<TransferStage>(stageName);
        await using ProviderCaptureServer server = await ProviderCaptureServer.StartAsync(CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        SyntheticAttachmentByteSource source = NewSource();
        var root = NewSpoolRoot();
        try
        {
            // The write trigger sits past the opening segment on purpose: at one
            // byte the streaming arm would be cancelled before it ever opened the
            // attachment, and the case that matters is the one where a read is
            // live when the cancellation lands.
            var afterBytes = stage is TransferStage.HttpWrite ? 100_000 : 1;
            using var interrupter = TransferInterrupter.With(
                CancellationToken.None, TransferInterruption.CancelAt(stage, afterBytes));
            var plan = new TransferPlan(
                armId, MailSendEnvelope.Default, [source], "probe-api-key", root, true, interrupter);

            await Should.ThrowAsync<OperationCanceledException>(
                () => ProviderTransferArms.SendAsync(client, plan, interrupter.Token));

            interrupter.CancelledAt.ShouldBe(stage);
            source.OpenStreams.ShouldBe(0, "uma leitura da fonte continuou aberta após o cancelamento");
            source.StreamsOpened.ShouldBeGreaterThan(0);
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
            server.Calls.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        // On Windows the removal is itself the proof: a directory holding a file
        // with an open handle does not go away.
        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact]
    public async Task A_read_that_fails_midway_still_removes_the_body_the_spool_arm_had_written()
    {
        await using ProviderCaptureServer server = await ProviderCaptureServer.StartAsync(CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        SyntheticAttachmentByteSource source = NewSource();
        var root = NewSpoolRoot();
        try
        {
            using var interrupter = TransferInterrupter.With(
                CancellationToken.None, TransferInterruption.FaultAt(TransferStage.Encode, 1));
            var plan = new TransferPlan(
                ProviderTransferArms.SpoolArm,
                MailSendEnvelope.Default,
                [source],
                "probe-api-key",
                root,
                true,
                interrupter);

            await Should.ThrowAsync<IOException>(
                () => ProviderTransferArms.SendAsync(client, plan, interrupter.Token));

            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
            source.OpenStreams.ShouldBe(0);
            server.Calls.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Backpressure_on_the_write_does_not_change_the_bytes_the_provider_receives()
    {
        await using ProviderCaptureServer server = await ProviderCaptureServer.StartAsync(CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        SyntheticAttachmentByteSource source = NewSource();
        var root = NewSpoolRoot();
        try
        {
            using var unhindered = TransferInterrupter.Idle(CancellationToken.None);
            await ProviderTransferArms.SendAsync(
                client, Plan(ProviderTransferArms.StreamingArm, source, root, unhindered), unhindered.Token);

            server.BodyReadDelay = TimeSpan.FromMilliseconds(2);
            using var slowed = TransferInterrupter.With(
                CancellationToken.None,
                TransferInterruption.BackpressureFrom(
                    TransferStage.HttpWrite, 0, TimeSpan.FromMilliseconds(2)));
            await ProviderTransferArms.SendAsync(
                client, Plan(ProviderTransferArms.StreamingArm, source, root, slowed), slowed.Token);

            server.Calls.Count.ShouldBe(2);
            server.Calls[1].BodySha256.ShouldBe(server.Calls[0].BodySha256);
            server.Calls[1].BodyBytes.ShouldBe(server.Calls[0].BodyBytes);
            source.OpenStreams.ShouldBe(0);
        }
        finally
        {
            server.BodyReadDelay = TimeSpan.Zero;
            Directory.Delete(root, recursive: true);
        }
    }

    private static TransferPlan Plan(
        string armId,
        IAttachmentByteSource source,
        string root,
        TransferInterrupter interrupter)
        => new(armId, MailSendEnvelope.Default, [source], "probe-api-key", root, true, interrupter);

    private static SyntheticAttachmentByteSource NewSource()
        => new(AttachmentBytes, "comprovante-0.pdf", "application/pdf", 16 * 1_024, TimeSpan.Zero);

    private static string NewSpoolRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"notification-hub-transfer-test-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}");
        Directory.CreateDirectory(root);
        return root;
    }
}
