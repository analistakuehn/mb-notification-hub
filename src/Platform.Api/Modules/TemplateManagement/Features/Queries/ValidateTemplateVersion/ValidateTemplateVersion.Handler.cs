using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class ValidateTemplateVersion
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        ScribanTemplateEngine engine,
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

            var analyses = version.Contents
                .Select(content => new ContentAnalysis(content.Channel, content.Locale, AnalyzeFields(content)))
                .ToList();

            // The report is the value this use case produces: running the
            // validation succeeds even when checks fail, so failed checks
            // travel in the response, never in the error string.
            ValidationReport report = TemplateValidation.Validate(template, version, analyses);
            int failed = report.Checks.Count(check => check.Status == ValidationCheckStatuses.Failed);
            logger.VersionValidated(version.TemplateKey.Value, version.Version, report.Passed, failed);
            return Result.Success(Response.From(report));
        }

        private List<ContentFieldAnalysis> AnalyzeFields(TemplateContent content)
        {
            List<ContentFieldAnalysis> fields = [];
            if (!string.IsNullOrEmpty(content.Subject))
            {
                fields.Add(Analyze(TemplateContentFields.Subject, content.Subject));
            }

            fields.Add(Analyze(TemplateContentFields.Body, content.Body));
            if (!string.IsNullOrEmpty(content.BodyText))
            {
                fields.Add(Analyze(TemplateContentFields.BodyText, content.BodyText));
            }

            return fields;
        }

        private ContentFieldAnalysis Analyze(string field, string source)
        {
            TemplateSourceAnalysis analysis = engine.Analyze(source, field);
            return new ContentFieldAnalysis(field, analysis.ParseSucceeded, analysis.ParseError, analysis.UsedVariables);
        }
    }
}
