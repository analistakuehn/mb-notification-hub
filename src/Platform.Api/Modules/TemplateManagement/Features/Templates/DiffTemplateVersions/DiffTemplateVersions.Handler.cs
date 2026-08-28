using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class DiffTemplateVersions
{
    internal sealed class Handler(TemplateManagementDbContext dbContext)
    {
        public async Task<Result<Response>> HandleAsync(
            string key,
            int versionNumber,
            int againstVersion,
            CancellationToken cancellationToken)
        {
            Result<TemplateKey> templateKey = TemplateKey.Create(key);
            if (templateKey.IsFailure)
            {
                return templateKey.AsFailure<TemplateKey, Response>();
            }

            TemplateVersion? baseVersion = await FindVersionAsync(templateKey.Value!, versionNumber, cancellationToken);
            if (baseVersion is null)
            {
                return VersionNotFound(templateKey.Value!, versionNumber);
            }

            TemplateVersion? against = await FindVersionAsync(templateKey.Value!, againstVersion, cancellationToken);
            if (against is null)
            {
                return VersionNotFound(templateKey.Value!, againstVersion);
            }

            ContentSetDiff contents = VersionDiff.DiffContents(FieldSets(baseVersion), FieldSets(against));
            SchemaFieldDiff schema = VersionDiff.DiffVariablesSchemas(
                baseVersion.VariablesSchemaJson,
                against.VariablesSchemaJson);
            return Result.Success(Response.From(
                templateKey.Value!, versionNumber, againstVersion, contents, schema));
        }

        private async Task<TemplateVersion?> FindVersionAsync(
            TemplateKey key,
            int versionNumber,
            CancellationToken cancellationToken)
            => await dbContext.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(key)
                .FirstOrDefaultAsync(candidate => candidate.Version == versionNumber, cancellationToken);

        private static Result<Response> VersionNotFound(TemplateKey key, int versionNumber)
            => Result.NotFound<Response>(DomainError.Format(
                ErrorCodes.TemplateVersionNotFound,
                $"Template '{key.Value}' has no version {versionNumber}."));

        private static List<ContentFieldSet> FieldSets(TemplateVersion version)
            => version.Contents
                .Select(content => new ContentFieldSet(
                    content.Channel.Value,
                    content.Locale.Value,
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        [TemplateContentFields.Subject] = content.Subject,
                        [TemplateContentFields.Body] = content.Body,
                        [TemplateContentFields.BodyText] = content.BodyText,
                    }))
                .ToList();
    }
}
