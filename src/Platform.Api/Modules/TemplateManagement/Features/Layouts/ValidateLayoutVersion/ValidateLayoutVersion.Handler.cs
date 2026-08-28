using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class ValidateLayoutVersion
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        LayoutVersionAnalyzer analyzer,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(
            string key,
            int versionNumber,
            CancellationToken cancellationToken)
        {
            Result<LayoutKey> layoutKey = LayoutKey.Create(key);
            if (layoutKey.IsFailure)
            {
                return layoutKey.AsFailure<LayoutKey, Response>();
            }

            var layoutExists = await dbContext.Layouts
                .AsNoTracking()
                .WhereKey(layoutKey.Value!)
                .AnyAsync(cancellationToken);
            if (!layoutExists)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.LayoutNotFound,
                    $"Layout '{layoutKey.Value!.Value}' does not exist."));
            }

            LayoutVersion? version = await dbContext.LayoutVersions
                .AsNoTracking()
                .WhereLayoutKey(layoutKey.Value!)
                .FirstOrDefaultAsync(candidate => candidate.Version == versionNumber, cancellationToken);
            if (version is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.LayoutVersionNotFound,
                    $"Layout '{layoutKey.Value!.Value}' has no version {versionNumber}."));
            }

            // The report is the value this use case produces: running the
            // validation succeeds even when checks fail, so failed checks
            // travel in the response, never in the error string.
            ValidationReport report = LayoutValidation.Validate(version, analyzer.Analyze(version));
            var failed = report.Checks.Count(check => check.Status == ValidationCheckStatuses.Failed);
            logger.LayoutVersionValidated(version.LayoutKey.Value, version.Version, report.Passed, failed);
            return Result.Success(Response.From(report));
        }
    }
}
