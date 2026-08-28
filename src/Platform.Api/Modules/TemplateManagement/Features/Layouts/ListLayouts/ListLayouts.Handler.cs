using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class ListLayouts
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    internal sealed class Handler(TemplateManagementDbContext dbContext)
    {
        public async Task<Result<Response>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            if (query.Limit is < 1 or > MaxPageSize)
            {
                return Result.ValidationError<Response>(DomainError.Format(
                    ErrorCodes.InvalidRequest,
                    $"limit must be between 1 and {MaxPageSize}."));
            }

            var pageSize = query.Limit ?? DefaultPageSize;
            IQueryable<Layout> layouts = dbContext.Layouts.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(query.Status))
            {
                Result<LayoutStatus> status = LayoutStatuses.Create(query.Status);
                if (status.IsFailure)
                {
                    return status.AsFailure<LayoutStatus, Response>();
                }

                LayoutStatus statusFilter = status.Value;
                layouts = layouts.Where(layout => layout.Status == statusFilter);
            }

            if (!string.IsNullOrWhiteSpace(query.Owner))
            {
                layouts = layouts.Where(layout => layout.OwnerTeam == query.Owner);
            }

            if (!string.IsNullOrWhiteSpace(query.Cursor))
            {
                Result<string> lastKey = PageCursor.Decode(query.Cursor);
                if (lastKey.IsFailure)
                {
                    return lastKey.AsFailure<string, Response>();
                }

                layouts = layouts.WhereKeyAfter(lastKey.Value!);
            }

            List<Layout> page = await layouts
                .OrderByKey()
                .Take(pageSize + 1)
                .ToListAsync(cancellationToken);

            var hasMore = page.Count > pageSize;
            var items = page
                .Take(pageSize)
                .Select(Item.From)
                .ToList();
            var nextCursor = hasMore ? PageCursor.Encode(items[^1].Key) : null;

            return Result.Success(new Response(items, nextCursor));
        }
    }
}
