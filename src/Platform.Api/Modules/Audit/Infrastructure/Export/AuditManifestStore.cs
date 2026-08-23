using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Export;

/// <summary>
/// Addressing and reading of manifests. The bucket needs no index: keys are
/// derived from the partition and the window, so both the exporter and a
/// verifier find the same object from the same facts, and the predecessor of
/// an export is discovered by looking where it must be rather than by trusting
/// a pointer kept somewhere else.
/// </summary>
internal sealed class AuditManifestStore(IWormObjectStore store, IOptions<WormExportOptions> options)
{
    internal string DailyFolder(string partition, DateOnly day)
        => AuditExportKeys.DailyFolder(
            options.Value.KeyPrefix, AuditPartitionCatalog.Table, partition, day);

    internal string ClosingFolder(string partition)
        => AuditExportKeys.ClosingFolder(options.Value.KeyPrefix, AuditPartitionCatalog.Table, partition);

    internal Task<bool> ExistsAsync(string folder, CancellationToken cancellationToken)
        => ExistsObjectAsync(folder + AuditExportKeys.ManifestObject, cancellationToken);

    internal async Task<AuditExportManifest?> TryReadAsync(string folder, CancellationToken cancellationToken)
    {
        var content = await store.GetAsync(folder + AuditExportKeys.ManifestObject, cancellationToken);
        return content is null ? null : AuditExportManifest.Parse(content);
    }

    /// <summary>
    /// The last manifest written for a partition: its closing export when the
    /// partition is already closed, otherwise the most recent day that has
    /// one. It is what a new export links back to, which is what makes a
    /// removed export detectable instead of merely absent.
    /// </summary>
    internal async Task<(string Folder, AuditExportManifest Manifest)?> FindLastAsync(
        MonthlyPartitionWindow window,
        CancellationToken cancellationToken)
    {
        var closing = ClosingFolder(window.PartitionName);
        AuditExportManifest? manifest = await TryReadAsync(closing, cancellationToken);
        if (manifest is not null)
        {
            return (closing, manifest);
        }

        return await FindLastDailyAsync(window, cancellationToken);
    }

    /// <summary>
    /// The most recent daily slice of a partition. The closing export links to
    /// it and never to itself, so rerunning a closing cycle produces the same
    /// bytes it produced the first time.
    /// </summary>
    internal async Task<(string Folder, AuditExportManifest Manifest)?> FindLastDailyAsync(
        MonthlyPartitionWindow window,
        CancellationToken cancellationToken)
    {
        for (DateOnly day = window.ToExclusive.AddDays(-1); day >= window.FromInclusive; day = day.AddDays(-1))
        {
            var folder = DailyFolder(window.PartitionName, day);
            AuditExportManifest? manifest = await TryReadAsync(folder, cancellationToken);
            if (manifest is not null)
            {
                return (folder, manifest);
            }
        }

        return null;
    }

    private async Task<bool> ExistsObjectAsync(string key, CancellationToken cancellationToken)
        => await store.HeadAsync(key, cancellationToken) is not null;
}
