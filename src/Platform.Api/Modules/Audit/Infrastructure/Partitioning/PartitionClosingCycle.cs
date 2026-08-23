using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Export;
using NotificationHub.Api.Modules.Audit.Infrastructure.Verification;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

/// <summary>How far one partition got through the closing cycle, and why it stopped there.</summary>
internal sealed record PartitionClosingOutcome(string Partition, string Stage, bool Closed, string? Failure)
{
    internal const string RevokeStage = "revoke";
    internal const string VerifyStage = "verify";
    internal const string ExportStage = "export";
    internal const string CopyStage = "copy";
    internal const string DetachStage = "detach";
    internal const string DropStage = "drop";
    internal const string SkippedStage = "skipped";
}

/// <summary>
/// The closing cycle of a monthly partition, in the one order that keeps the
/// evidence ahead of the destruction: stop the writes, verify the chain whole,
/// export the partition, read the copy back and replay it, record the closing
/// in the trail, and only then detach. Destroying the detached table is a
/// separate step behind its own gate, so no single switch ever turns on both
/// exporting and destroying.
/// </summary>
/// <remarks>
/// Every stage that fails stops the cycle where it is. A partition that could
/// not be verified is never exported; a copy that could not be read back is
/// never detached; a table whose copy no longer verifies is never dropped.
/// The cost of stopping is a partition that stays in the database one more
/// cycle, which is the cheap side of this trade.
/// </remarks>
internal sealed class PartitionClosingCycle(
    ClosedPartitionGuard guard,
    ChainVerifier verifier,
    AuditClosingExporter closingExporter,
    AuditMaintenanceJournal journal,
    IOptions<PartitionManagerOptions> options,
    TimeProvider timeProvider,
    ILogger<PartitionClosingCycle> logger)
{
    /// <summary>Closes every partition past its grace period and, under the drop gate, destroys detached ones.</summary>
    public async Task<IReadOnlyList<PartitionClosingOutcome>> RunAsync(
        IReadOnlyList<MonthlyPartitionWindow> attached,
        IReadOnlyList<MonthlyPartitionWindow> detached,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attached);
        ArgumentNullException.ThrowIfNull(detached);
        PartitionManagerOptions settings = options.Value;
        if (!settings.EnableRetentionCycle)
        {
            logger.RetentionCycleInactive();
            return [];
        }

        var outcomes = new List<PartitionClosingOutcome>();
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (settings.EnableRevokeOnClosedPartitions)
        {
            foreach (MonthlyPartitionWindow window in attached.Where(window => window.ToExclusive > today))
            {
                await guard.EnsureAppenderGrantAsync(window, cancellationToken);
            }
        }

        foreach (MonthlyPartitionWindow window in attached.Where(window => IsClosable(window, today, settings)))
        {
            outcomes.Add(await CloseAsync(window, settings, cancellationToken));
        }

        foreach (MonthlyPartitionWindow window in detached)
        {
            outcomes.Add(await DropIfAllowedAsync(window, settings, today, cancellationToken));
        }

        return outcomes;
    }

    private async Task<PartitionClosingOutcome> CloseAsync(
        MonthlyPartitionWindow window,
        PartitionManagerOptions settings,
        CancellationToken cancellationToken)
    {
        if (!settings.EnableRevokeOnClosedPartitions)
        {
            // Without the write revoke the partition can still receive rows
            // after it was verified and exported, so the whole cycle waits.
            logger.RevokeStepInactive();
            return new PartitionClosingOutcome(
                window.PartitionName, PartitionClosingOutcome.SkippedStage, false, "revoke-gate-disabled");
        }

        await guard.RevokeWritesAsync(window, cancellationToken);

        ChainVerificationOutcome verification = await verifier.VerifyAsync(
            window, forceFullReplay: true, cancellationToken);
        if (!verification.IsIntact)
        {
            logger.ClosingAborted(window.PartitionName, PartitionClosingOutcome.VerifyStage, verification.Failure!);
            return new PartitionClosingOutcome(
                window.PartitionName, PartitionClosingOutcome.VerifyStage, false, verification.Failure);
        }

        ClosingExportResult export = await closingExporter.ExportAndVerifyAsync(window, cancellationToken);
        if (!export.CopyVerified)
        {
            logger.ClosingAborted(
                window.PartitionName, PartitionClosingOutcome.CopyStage, export.CopyFailure ?? "unknown");
            await journal.RecordAsync(
                AuditActions.AuditChainVerificationFailed,
                window.PartitionName,
                [
                    ("stage", PartitionClosingOutcome.CopyStage),
                    ("manifestKey", export.ManifestKey),
                    ("failure", export.CopyFailure),
                ],
                cancellationToken);
            return new PartitionClosingOutcome(
                window.PartitionName, PartitionClosingOutcome.CopyStage, false, export.CopyFailure);
        }

        await journal.RecordAsync(
            AuditActions.AuditPartitionClosed,
            window.PartitionName,
            [
                ("manifestKey", export.ManifestKey),
                ("tailHash", export.Manifest!.TailHash),
                ("chainedCount", export.Manifest.ChainedCount),
                ("unchainedCount", export.Manifest.UnchainedCount),
                ("seqMin", export.Manifest.SeqMin),
                ("seqMax", export.Manifest.SeqMax),
            ],
            cancellationToken);

        await guard.DetachAsync(window, cancellationToken);
        return new PartitionClosingOutcome(
            window.PartitionName, PartitionClosingOutcome.DetachStage, true, null);
    }

    private async Task<PartitionClosingOutcome> DropIfAllowedAsync(
        MonthlyPartitionWindow window,
        PartitionManagerOptions settings,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        if (!settings.EnableDropDetachedPartitions)
        {
            logger.DropStepInactive(window.PartitionName);
            return new PartitionClosingOutcome(
                window.PartitionName, PartitionClosingOutcome.SkippedStage, false, "drop-gate-disabled");
        }

        if (window.ToExclusive.AddDays(settings.DatabaseResidencyDays) > today)
        {
            return new PartitionClosingOutcome(
                window.PartitionName, PartitionClosingOutcome.SkippedStage, false, "within-database-residency");
        }

        WormVerificationResult copy = await closingExporter.VerifyClosingCopyAsync(window, cancellationToken);
        if (!copy.IsValid)
        {
            logger.ClosingAborted(
                window.PartitionName, PartitionClosingOutcome.DropStage, copy.Failure ?? "unknown");
            return new PartitionClosingOutcome(
                window.PartitionName, PartitionClosingOutcome.DropStage, false, copy.Failure);
        }

        await guard.DropAsync(window, cancellationToken);
        return new PartitionClosingOutcome(
            window.PartitionName, PartitionClosingOutcome.DropStage, true, null);
    }

    /// <summary>
    /// A partition closes once its month is over and the grace period has run
    /// out. The grace period is what keeps a slow effect from arriving after
    /// its partition was declared final.
    /// </summary>
    private static bool IsClosable(MonthlyPartitionWindow window, DateOnly today, PartitionManagerOptions settings)
        => window.ToExclusive.AddDays(settings.ClosingGraceDays) <= today;
}
