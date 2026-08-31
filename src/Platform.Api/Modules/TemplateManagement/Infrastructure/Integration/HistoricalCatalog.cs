using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

/// <summary>
/// Reads one exact version of the catalog, never the current one. Nothing here
/// is memoized as a "current published" pointer: a version is immutable once it
/// leaves draft, and the point of the read is that it does not move when a new
/// version is published on top of it.
/// </summary>
/// <remarks>
/// The read answers for published and superseded state only, on both the
/// version and the layout it pinned. A draft never rendered anything: the
/// lifecycle runs draft, published, superseded and never back, and only a
/// published version renders, so a version that is a draft today cannot have
/// produced the notification that names it. A pinned layout draft is
/// impossible for the same reason on a second step, since publishing a
/// template version requires the pinned layout version to be published.
/// Neither state is therefore reachable through a legitimate path, and that is
/// exactly why reaching one is logged rather than absorbed.
/// </remarks>
internal sealed class HistoricalCatalog(
    TemplateManagementDbContext dbContext,
    ILogger<HistoricalCatalog> logger) : IHistoricalCatalog
{
    public async Task<Result<HistoricalTemplateVersion>> FindTemplateVersionAsync(
        string application,
        string templateKey,
        int version,
        CancellationToken cancellationToken)
    {
        Result<(Template Template, TemplateKey Key)> lookup =
            await dbContext.FindApplicationTemplateAsync(application, templateKey, cancellationToken);
        if (lookup.IsFailure)
        {
            return lookup.AsFailure<(Template, TemplateKey), HistoricalTemplateVersion>();
        }

        // A deprecated or disabled identity still answers here: the question is
        // what an old notification rendered, not what a new one may request.
        (Template template, TemplateKey key) = lookup.Value;
        TemplateVersion? historical = await dbContext.TemplateVersions
            .AsNoTracking()
            .WhereTemplateKey(key)
            .FirstOrDefaultAsync(candidate => candidate.Version == version, cancellationToken);
        if (historical is null)
        {
            return VersionNotFound(key, version);
        }

        // A version that never left draft is not part of what shipped, so it
        // does not leave through this surface. The caller reads the same
        // not-found it reads for a version the store never had, because the
        // difference is not one it can act on; the log below is where the
        // difference stays audible, and without it the answer would trade a
        // wrong template block for a silent one.
        if (!IsPublishedOrSuperseded(historical.Status))
        {
            logger.TemplateVersionWithheld(
                template.Application, key.Value, version, historical.Status.Canonical());
            return VersionNotFound(key, version);
        }

        (HistoricalLayoutPin? pin, HistoricalLayoutVersion? layout) =
            await ReadPinnedLayoutAsync(key, historical, cancellationToken);
        return Result.Success(new HistoricalTemplateVersion
        {
            Application = template.Application,
            TemplateKey = key.Value,
            Version = historical.Version,
            VersionStatus = historical.Status.Canonical(),
            TemplateStatus = template.Status.Canonical(),
            Class = template.Class.Canonical(),
            OwnerTeam = template.OwnerTeam,
            Purpose = template.Purpose,
            LegalBasis = template.LegalBasis,
            SensitiveVariables = [.. historical.SensitiveVariables],
            ContentHash = historical.ContentHash,
            PublishedAt = historical.PublishedAt,
            RolledBackFromVersion = historical.RolledBackFrom,
            LayoutPin = pin,
            Layout = layout,
        });
    }

    /// <summary>
    /// The one not-found this surface answers with. A version the store never
    /// had and a version that never left draft read the same way here on
    /// purpose: the caller reconstructs what shipped, and neither of the two
    /// shipped.
    /// </summary>
    private static Result<HistoricalTemplateVersion> VersionNotFound(TemplateKey key, int version)
        => Result.NotFound<HistoricalTemplateVersion>(DomainError.Format(
            ErrorCodes.TemplateVersionNotFound,
            $"Template '{key.Value}' has no version {version}."));

    /// <summary>
    /// The lifecycle this surface answers for, written as the two statuses it
    /// admits and not as "anything but draft", so a status a later lifecycle
    /// adds stays out until somebody puts it here on purpose.
    /// </summary>
    private static bool IsPublishedOrSuperseded(TemplateVersionStatus status)
        => status is TemplateVersionStatus.Published or TemplateVersionStatus.Superseded;

    private static bool IsPublishedOrSuperseded(LayoutVersionStatus status)
        => status is LayoutVersionStatus.Published or LayoutVersionStatus.Superseded;

    /// <summary>
    /// Reads the layout side of the answer as two facts instead of one. The pin
    /// is what the version declared and it travels whether or not it resolves;
    /// the layout is what the pin resolved to and it travels only when this
    /// surface can vouch for it. Collapsing the two into a single omission made
    /// "this message was framed by nothing" and "this message was framed and the
    /// hash of the frame is unknown" read the same way, which is a wrong answer
    /// to a compliance question and not a partial one.
    /// </summary>
    private async Task<(HistoricalLayoutPin? Pin, HistoricalLayoutVersion? Layout)> ReadPinnedLayoutAsync(
        TemplateKey key,
        TemplateVersion version,
        CancellationToken cancellationToken)
    {
        // The one legitimate absence on this axis: nothing was pinned, so there
        // is no pin to declare and no layout to resolve.
        if (version.LayoutKey is not string layoutKey)
        {
            return (null, null);
        }

        var pinned = version.LayoutVersion!.Value;
        var pin = new HistoricalLayoutPin { LayoutKey = layoutKey, Version = pinned };
        LayoutVersion? layout = await dbContext.LayoutVersions
            .AsNoTracking()
            .WhereLayoutKey(LayoutKey.Trusted(layoutKey))
            .FirstOrDefaultAsync(candidate => candidate.Version == pinned, cancellationToken);

        // A pin that no longer resolves is itself evidence: the answer declares
        // the pin and omits the layout instead of inventing a hash for it.
        // Nothing deletes a layout version through this module, so the row was
        // taken out from outside it, and the log is what says so.
        if (layout is null)
        {
            logger.PinnedLayoutVersionMissing(layoutKey, pinned, key.Value, version.Version);
            return (pin, null);
        }

        // A pinned layout that never left draft could not have wrapped the
        // message either, since publishing this version required the pin to
        // resolve to a published layout version. It leaves the same shape a pin
        // that no longer resolves leaves, pin declared and layout omitted, and
        // the log is what keeps the two apart.
        if (!IsPublishedOrSuperseded(layout.Status))
        {
            logger.PinnedLayoutVersionWithheld(
                layoutKey, layout.Version, key.Value, version.Version, layout.Status.Canonical());
            return (pin, null);
        }

        return (pin, new HistoricalLayoutVersion
        {
            LayoutKey = layoutKey,
            Version = layout.Version,
            VersionStatus = layout.Status.Canonical(),
            ContentHash = layout.ContentHash,
            PublishedAt = layout.PublishedAt,
        });
    }
}
