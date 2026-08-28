using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class DiffLayoutVersions
{
    internal sealed class Handler(TemplateManagementDbContext dbContext)
    {
        public async Task<Result<Response>> HandleAsync(
            string key,
            int versionNumber,
            int againstVersion,
            CancellationToken cancellationToken)
        {
            Result<LayoutKey> layoutKey = LayoutKey.Create(key);
            if (layoutKey.IsFailure)
            {
                return layoutKey.AsFailure<LayoutKey, Response>();
            }

            LayoutVersion? baseVersion = await FindVersionAsync(layoutKey.Value!, versionNumber, cancellationToken);
            if (baseVersion is null)
            {
                return VersionNotFound(layoutKey.Value!, versionNumber);
            }

            LayoutVersion? against = await FindVersionAsync(layoutKey.Value!, againstVersion, cancellationToken);
            if (against is null)
            {
                return VersionNotFound(layoutKey.Value!, againstVersion);
            }

            ContentSetDiff contents = VersionDiff.DiffContents(FieldSets(baseVersion), FieldSets(against));
            return Result.Success(Response.From(layoutKey.Value!, versionNumber, againstVersion, contents));
        }

        private async Task<LayoutVersion?> FindVersionAsync(
            LayoutKey key,
            int versionNumber,
            CancellationToken cancellationToken)
            => await dbContext.LayoutVersions
                .AsNoTracking()
                .WhereLayoutKey(key)
                .FirstOrDefaultAsync(candidate => candidate.Version == versionNumber, cancellationToken);

        private static Result<Response> VersionNotFound(LayoutKey key, int versionNumber)
            => Result.NotFound<Response>(DomainError.Format(
                ErrorCodes.LayoutVersionNotFound,
                $"Layout '{key.Value}' has no version {versionNumber}."));

        private static List<ContentFieldSet> FieldSets(LayoutVersion version)
            => version.Contents
                .Select(content => new ContentFieldSet(
                    content.Channel.Value,
                    content.Locale.Value,
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        [TemplateContentFields.Body] = content.Body,
                        [TemplateContentFields.BodyText] = content.BodyText,
                    }))
                .ToList();
    }
}
