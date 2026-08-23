using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Export;

/// <summary>
/// Outcome of the authoritative export of a partition, including the verdict
/// of reading the copy back.
/// </summary>
internal sealed record ClosingExportResult(
    string ManifestKey,
    AuditExportManifest? Manifest,
    bool CopyVerified,
    string? CopyFailure);

/// <summary>
/// The authoritative export of a whole partition, plus the check that the
/// copy is readable and complete. The daily slices already carry the same
/// rows, but the closing file restates the partition from its anchor to its
/// final hash in one artifact, which is what an auditor is handed years later.
/// </summary>
/// <remarks>
/// The copy is verified by reading the written objects back and replaying the
/// chain from the exported bytes, not from the database. Anything less would
/// prove that the export process believes itself, which is not evidence.
/// </remarks>
internal sealed class AuditClosingExporter(
    AuditTrailReader reader,
    AuditExporter exporter,
    AuditManifestStore manifests,
    WormExportVerifier verifier,
    IOptions<WormExportOptions> options)
{
    public async Task<ClosingExportResult> ExportAndVerifyAsync(
        MonthlyPartitionWindow window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        var folder = manifests.ClosingFolder(window.PartitionName);
        if (!options.Value.EnableClosingExport)
        {
            // Without the authoritative copy there is nothing to verify, and
            // without a verified copy nothing downstream may proceed.
            return new ClosingExportResult(
                folder + AuditExportKeys.ManifestObject, null, false, "closing-export-disabled");
        }

        (string Folder, AuditExportManifest Manifest)? previous =
            await manifests.FindLastDailyAsync(window, cancellationToken);
        var throughSeq = await reader.MaxSeqAsync(window, cancellationToken);

        var request = new AuditExportRequest(
            window,
            AuditExportManifest.ClosingType,
            ToInstant(window.FromInclusive),
            ToInstant(window.ToExclusive),
            folder,
            AfterSeq: 0,
            throughSeq,
            AuditChain.PartitionAnchor(window.PartitionName),
            previous is null
                ? null
                : new AuditExportManifestLink(
                    previous.Value.Folder + AuditExportKeys.ManifestObject,
                    previous.Value.Manifest.Partition,
                    previous.Value.Manifest.TailHash));

        AuditExportResult result = await exporter.ExportAsync(request, cancellationToken);
        var manifestKey = folder + AuditExportKeys.ManifestObject;
        WormVerificationResult copy = await verifier.VerifyAsync(manifestKey, cancellationToken);
        return new ClosingExportResult(manifestKey, result.Manifest, copy.IsValid, copy.Failure);
    }

    /// <summary>
    /// Re-reads the authoritative copy of a partition and replays it. The
    /// destructive end of the retention cycle asks this again, right before
    /// acting: a copy that verified weeks ago is not evidence that the copy is
    /// still there today.
    /// </summary>
    public async Task<WormVerificationResult> VerifyClosingCopyAsync(
        MonthlyPartitionWindow window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        var manifestKey = manifests.ClosingFolder(window.PartitionName) + AuditExportKeys.ManifestObject;
        return await verifier.VerifyAsync(manifestKey, cancellationToken);
    }

    private static DateTimeOffset ToInstant(DateOnly day)
        => new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
}
