using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;

/// <summary>
/// Loads from the store the facts the layout-reference validation check needs
/// about the layout a template version pins. Validate, publish and rollback
/// share this path so the check a publisher sees is the one that gates them.
/// </summary>
internal static class LayoutReferenceQueries
{
    internal static async Task<LayoutReferenceFacts?> LoadLayoutReferenceAsync(
        this TemplateManagementDbContext dbContext,
        TemplateVersion version,
        CancellationToken cancellationToken)
    {
        if (version.LayoutKey is not string layoutKey)
        {
            return null;
        }

        var key = LayoutKey.Trusted(layoutKey);
        var pinnedVersion = version.LayoutVersion!.Value;

        Layout? layout = await dbContext.Layouts
            .AsNoTracking()
            .WhereKey(key)
            .FirstOrDefaultAsync(cancellationToken);
        LayoutVersion? pinned = await dbContext.LayoutVersions
            .AsNoTracking()
            .WhereLayoutKey(key)
            .FirstOrDefaultAsync(candidate => candidate.Version == pinnedVersion, cancellationToken);

        return new LayoutReferenceFacts
        {
            LayoutKey = layoutKey,
            LayoutVersion = pinnedVersion,
            LayoutExists = layout is not null,
            VersionExists = pinned is not null,
            VersionStatus = pinned?.Status.Canonical(),
            DefaultLocale = layout?.DefaultLocale?.Value,
            Contents = pinned?.Contents
                .Select(content => new ContentUnit(content.Channel.Value, content.Locale.Value))
                .ToList() ?? [],
        };
    }
}
