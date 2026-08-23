using System.Globalization;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Export;

/// <summary>
/// Plans and runs the daily slices. It walks each open partition day by day,
/// skips what is already exported, and exports the rest in order, so the
/// manifests of a partition form a chain that a later reader can follow
/// backwards.
/// </summary>
/// <remarks>
/// A slice carries the contiguous sequence range that ends at the day's
/// highest sequence, not literally the rows whose occurrence instant falls in
/// the day. The two differ only when an effect commits with an occurrence
/// instant older than one already committed, and when they differ the
/// sequence range is the one that matters: it is the order the chain was
/// built in, and only a contiguous range can be replayed from a head hash to
/// a tail hash without carrying a hash per line.
/// </remarks>
internal sealed class AuditExportPlanner(
    AuditTrailReader reader,
    AuditExporter exporter,
    AuditManifestStore manifests,
    IOptions<WormExportOptions> options,
    TimeProvider timeProvider,
    ILogger<AuditExportPlanner> logger)
{
    /// <summary>Exports every stabilized day of the given partitions; returns how many slices this round wrote.</summary>
    public async Task<int> RunDailyAsync(
        IReadOnlyList<MonthlyPartitionWindow> partitions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        if (!options.Value.EnableDailyExport)
        {
            return 0;
        }

        var written = 0;
        foreach (MonthlyPartitionWindow window in partitions)
        {
            written += await ExportPartitionDaysAsync(window, cancellationToken);
        }

        return written;
    }

    private async Task<int> ExportPartitionDaysAsync(
        MonthlyPartitionWindow window,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        (string Folder, AuditExportManifest Manifest)? previous = null;
        var previousResolved = false;
        var written = 0;

        for (DateOnly day = window.FromInclusive; day < window.ToExclusive; day = day.AddDays(1))
        {
            if (!IsStabilized(day, now))
            {
                break;
            }

            var folder = manifests.DailyFolder(window.PartitionName, day);
            AuditExportManifest? existing = await manifests.TryReadAsync(folder, cancellationToken);
            if (existing is not null)
            {
                previous = (folder, existing);
                previousResolved = true;
                continue;
            }

            if (!previousResolved)
            {
                previous = await ResolvePredecessorAsync(window, cancellationToken);
                previousResolved = true;
            }

            AuditExportManifest? sameChain = SameChain(previous, window);
            var afterSeq = sameChain?.SeqMax ?? 0;
            var dayMaxSeq = await reader.MaxSeqOfDayAsync(day, cancellationToken);
            if (dayMaxSeq <= afterSeq)
            {
                // Nothing new closed on this day: no object, no manifest, and
                // therefore no gap in the chain of manifests either.
                continue;
            }

            var request = new AuditExportRequest(
                window,
                AuditExportManifest.DailyType,
                ToInstant(day),
                ToInstant(day.AddDays(1)),
                folder,
                afterSeq,
                dayMaxSeq,
                sameChain is null
                    ? AuditChain.PartitionAnchor(window.PartitionName)
                    : AuditHex.FromHex(sameChain.TailHash),
                Link(previous));

            AuditExportResult result = await exporter.ExportAsync(request, cancellationToken);
            previous = (folder, result.Manifest);
            written += result.AlreadyPresent ? 0 : 1;
            var exportedDay = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            logger.DailyExportCompleted(
                window.PartitionName, exportedDay, result.Manifest.SeqMin, result.Manifest.SeqMax);
        }

        return written;
    }

    /// <summary>
    /// The manifest a partition's first slice links back to: the last one
    /// written for the previous month. The link crosses the partition
    /// boundary, the chain of hashes does not, which is why each partition
    /// still starts at its own deterministic anchor.
    /// </summary>
    private async Task<(string Folder, AuditExportManifest Manifest)?> ResolvePredecessorAsync(
        MonthlyPartitionWindow window,
        CancellationToken cancellationToken)
    {
        DateOnly previousMonth = window.FromInclusive.AddMonths(-1);
        MonthlyPartitionWindow previousPartition = MonthlyPartitions.Plan(
            TableOf(window.PartitionName), ToInstant(previousMonth), 0)[0];
        return await manifests.FindLastAsync(previousPartition, cancellationToken);
    }

    /// <summary>
    /// The predecessor only continues the hash chain when it belongs to the
    /// same partition and is itself a slice; a closing export restates the
    /// whole partition and never serves as the head of a later slice.
    /// </summary>
    private static AuditExportManifest? SameChain(
        (string Folder, AuditExportManifest Manifest)? previous,
        MonthlyPartitionWindow window)
        => previous is { Manifest: var manifest }
            && string.Equals(manifest.Partition, window.PartitionName, StringComparison.Ordinal)
            && string.Equals(manifest.Type, AuditExportManifest.DailyType, StringComparison.Ordinal)
                ? manifest
                : null;

    private static AuditExportManifestLink? Link((string Folder, AuditExportManifest Manifest)? previous)
        => previous is null
            ? null
            : new AuditExportManifestLink(
                previous.Value.Folder + AuditExportKeys.ManifestObject,
                previous.Value.Manifest.Partition,
                previous.Value.Manifest.TailHash);

    private bool IsStabilized(DateOnly day, DateTimeOffset now)
        => ToInstant(day.AddDays(1)) + options.Value.StabilizationDelay <= now;

    private static DateTimeOffset ToInstant(DateOnly day)
        => new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    private static string TableOf(string partitionName)
        => string.Join('_', partitionName.Split('_')[..^2]);
}
