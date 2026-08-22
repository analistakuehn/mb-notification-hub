using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class ListTemplates
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

            int pageSize = query.Limit ?? DefaultPageSize;
            IQueryable<Template> templates = dbContext.Templates.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(query.Application))
            {
                templates = templates.Where(template => template.Application == query.Application);
            }

            if (!string.IsNullOrWhiteSpace(query.Class))
            {
                Result<NotificationClass> notificationClass = NotificationClasses.Create(query.Class);
                if (notificationClass.IsFailure)
                {
                    return notificationClass.AsFailure<NotificationClass, Response>();
                }

                NotificationClass classFilter = notificationClass.Value;
                templates = templates.Where(template => template.Class == classFilter);
            }

            if (!string.IsNullOrWhiteSpace(query.Status))
            {
                Result<TemplateStatus> status = TemplateStatuses.Create(query.Status);
                if (status.IsFailure)
                {
                    return status.AsFailure<TemplateStatus, Response>();
                }

                TemplateStatus statusFilter = status.Value;
                templates = templates.Where(template => template.Status == statusFilter);
            }

            if (!string.IsNullOrWhiteSpace(query.Owner))
            {
                templates = templates.Where(template => template.OwnerTeam == query.Owner);
            }

            if (!string.IsNullOrWhiteSpace(query.Cursor))
            {
                Result<string> lastKey = PageCursor.Decode(query.Cursor);
                if (lastKey.IsFailure)
                {
                    return lastKey.AsFailure<string, Response>();
                }

                templates = templates.WhereKeyAfter(lastKey.Value!);
            }

            List<Template> page = await templates
                .OrderByKey()
                .Take(pageSize + 1)
                .ToListAsync(cancellationToken);

            bool hasMore = page.Count > pageSize;
            var items = page
                .Take(pageSize)
                .Select(Item.From)
                .ToList();
            string? nextCursor = hasMore ? PageCursor.Encode(items[^1].Key) : null;

            return Result.Success(new Response(items, nextCursor));
        }
    }
}
