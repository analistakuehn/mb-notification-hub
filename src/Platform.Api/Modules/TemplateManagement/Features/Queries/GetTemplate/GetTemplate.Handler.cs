using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class GetTemplate
{
    internal sealed class Handler(TemplateManagementDbContext dbContext)
    {
        public async Task<Result<Response>> HandleAsync(string key, CancellationToken cancellationToken)
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

            List<TemplateVersion> versions = await dbContext.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(templateKey.Value!)
                .OrderBy(version => version.Version)
                .ToListAsync(cancellationToken);
            var summaries = versions
                .Select(version => new VersionSummary(
                    version.Version,
                    version.Status.Canonical(),
                    version.ContentHash,
                    version.CreatedBy,
                    version.CreatedAt))
                .ToList();

            return Result.Success(Response.From(template, summaries));
        }
    }
}
