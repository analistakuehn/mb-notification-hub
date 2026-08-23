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
internal sealed class HistoricalCatalog(TemplateManagementDbContext dbContext) : IHistoricalCatalog
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
            return Result.NotFound<HistoricalTemplateVersion>(DomainError.Format(
                ErrorCodes.TemplateVersionNotFound,
                $"Template '{key.Value}' has no version {version}."));
        }

        HistoricalLayoutVersion? layout = await FindPinnedLayoutAsync(historical, cancellationToken);
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
            SensitiveVariables = [.. template.SensitiveVariables],
            ContentHash = historical.ContentHash,
            PublishedAt = historical.PublishedAt,
            RolledBackFromVersion = historical.RolledBackFrom,
            Layout = layout,
        });
    }

    private async Task<HistoricalLayoutVersion?> FindPinnedLayoutAsync(
        TemplateVersion version,
        CancellationToken cancellationToken)
    {
        if (version.LayoutKey is not string layoutKey)
        {
            return null;
        }

        var pinned = version.LayoutVersion!.Value;
        LayoutVersion? layout = await dbContext.LayoutVersions
            .AsNoTracking()
            .WhereLayoutKey(LayoutKey.Trusted(layoutKey))
            .FirstOrDefaultAsync(candidate => candidate.Version == pinned, cancellationToken);

        // A pin that no longer resolves is itself evidence: the answer omits
        // the layout instead of inventing a hash for it.
        return layout is null
            ? null
            : new HistoricalLayoutVersion
            {
                LayoutKey = layoutKey,
                Version = layout.Version,
                VersionStatus = layout.Status.Canonical(),
                ContentHash = layout.ContentHash,
                PublishedAt = layout.PublishedAt,
            };
    }
}
