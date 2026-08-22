using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class GetLayoutVersion
{
    internal sealed class Handler(TemplateManagementDbContext dbContext)
    {
        public async Task<Result<Response>> HandleAsync(
            string key,
            int version,
            CancellationToken cancellationToken)
        {
            Result<LayoutKey> layoutKey = LayoutKey.Create(key);
            if (layoutKey.IsFailure)
            {
                return layoutKey.AsFailure<LayoutKey, Response>();
            }

            LayoutVersion? found = await dbContext.LayoutVersions
                .AsNoTracking()
                .WhereLayoutKey(layoutKey.Value!)
                .FirstOrDefaultAsync(candidate => candidate.Version == version, cancellationToken);
            if (found is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.LayoutVersionNotFound,
                    $"Layout '{layoutKey.Value!.Value}' has no version {version}."));
            }

            return Result.Success(Response.From(found));
        }
    }
}
