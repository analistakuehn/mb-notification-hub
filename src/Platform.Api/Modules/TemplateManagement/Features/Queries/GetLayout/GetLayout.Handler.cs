using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class GetLayout
{
    internal sealed class Handler(TemplateManagementDbContext dbContext)
    {
        public async Task<Result<Response>> HandleAsync(string key, CancellationToken cancellationToken)
        {
            Result<LayoutKey> layoutKey = LayoutKey.Create(key);
            if (layoutKey.IsFailure)
            {
                return layoutKey.AsFailure<LayoutKey, Response>();
            }

            Layout? layout = await dbContext.Layouts
                .AsNoTracking()
                .WhereKey(layoutKey.Value!)
                .FirstOrDefaultAsync(cancellationToken);
            if (layout is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.LayoutNotFound,
                    $"Layout '{layoutKey.Value!.Value}' does not exist."));
            }

            List<LayoutVersion> versions = await dbContext.LayoutVersions
                .AsNoTracking()
                .WhereLayoutKey(layoutKey.Value!)
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

            return Result.Success(Response.From(layout, summaries));
        }
    }
}
