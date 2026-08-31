using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class ValidateTemplateVersion
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        TemplateVersionAnalyzer analyzer,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(
            string key,
            int versionNumber,
            CancellationToken cancellationToken)
        {
            Result<TemplateKey> templateKey = TemplateKey.Create(key);
            if (templateKey.IsFailure)
            {
                return templateKey.AsFailure<TemplateKey, Response>();
            }

            Template? template = await dbContext.Templates
                .AsNoTracking()
                .WhereKey(templateKey.Value!)
                .FirstOrDefaultAsync(cancellationToken);
            if (template is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.TemplateNotFound,
                    $"Template '{templateKey.Value!.Value}' does not exist."));
            }

            TemplateVersion? version = await dbContext.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(templateKey.Value!)
                .FirstOrDefaultAsync(candidate => candidate.Version == versionNumber, cancellationToken);
            if (version is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.TemplateVersionNotFound,
                    $"Template '{templateKey.Value!.Value}' has no version {versionNumber}."));
            }

            // The version in force, read here for the same reason the
            // publication reads it: a dry run that answered without it would
            // report green on a version publication then refuses, and the
            // author would meet the refusal only at the door.
            TemplateVersion? current = await dbContext.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(templateKey.Value!)
                .Where(candidate => candidate.Status == TemplateVersionStatus.Published)
                .FirstOrDefaultAsync(cancellationToken);

            // The report is the value this use case produces: running the
            // validation succeeds even when checks fail, so failed checks
            // travel in the response, never in the error string.
            LayoutReferenceFacts? layoutReference =
                await dbContext.LoadLayoutReferenceAsync(version, cancellationToken);
            ValidationReport report = TemplateValidation.Validate(
                template,
                version,
                analyzer.Analyze(version),
                layoutReference,
                current?.SensitiveVariables);
            var failed = report.Checks.Count(check => check.Status == ValidationCheckStatuses.Failed);
            logger.VersionValidated(version.TemplateKey.Value, version.Version, report.Passed, failed);
            return Result.Success(Response.From(report));
        }
    }
}
