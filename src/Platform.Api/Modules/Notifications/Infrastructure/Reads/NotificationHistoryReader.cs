using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Http;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;

/// <summary>Subject and paging inputs of one history read.</summary>
internal sealed record NotificationHistoryRequest
{
    /// <summary>Exact recipient identity; never a prefix and never a pattern.</summary>
    public string? RecipientId { get; init; }

    /// <summary>Exact correlation identity of one business transaction.</summary>
    public string? CorrelationId { get; init; }

    public string? Class { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public string? Cursor { get; init; }

    public int? Limit { get; init; }
}

/// <summary>
/// The keyset read behind both history routes: descending over creation
/// instant and identity, bounded by an explicit window, resumed by an opaque
/// cursor. The window is not a convenience: the indexes that serve these
/// routes are local to each monthly partition, so a read without bounds
/// becomes a scan of every partition the table ever had.
/// </summary>
internal sealed class NotificationHistoryReader(NotificationsReadDbContext db, TimeProvider clock)
{
    private sealed record Row(
        Guid Id,
        string Application,
        string RecipientId,
        string Class,
        string Status,
        string TemplateKey,
        int TemplateVersion,
        string? CorrelationId,
        DateTimeOffset CreatedAt);

    internal async Task<NotificationHistoryOutcome> ReadAsync(
        NotificationHistoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Limit is < 1 or > NotificationQueryContract.MaxPageSize)
        {
            return new NotificationHistoryOutcome.InvalidRequest(
                $"O parâmetro limit precisa estar entre 1 e {NotificationQueryContract.MaxPageSize}.");
        }

        if (!string.IsNullOrWhiteSpace(request.Class) && !NotificationClasses.IsCanonical(request.Class))
        {
            return new NotificationHistoryOutcome.InvalidRequest(
                "O parâmetro class precisa ser uma das classes canônicas.");
        }

        if (!NotificationQueryWindow.TryResolve(
                request.From, request.To, clock.GetUtcNow(), out NotificationQueryWindow window, out var windowError))
        {
            return new NotificationHistoryOutcome.InvalidRequest(windowError!);
        }

        NotificationQueryPosition? position = null;
        if (!string.IsNullOrWhiteSpace(request.Cursor))
        {
            if (!NotificationQueryCursor.TryDecode(request.Cursor, out NotificationQueryPosition decoded))
            {
                return new NotificationHistoryOutcome.InvalidCursor(
                    "O cursor não é válido. Use o nextCursor devolvido pela página anterior.");
            }

            // The cursor carries a position, never a window: a position
            // outside the window asked for would silently page through rows
            // the caller did not ask about.
            if (!window.Contains(decoded.CreatedAt))
            {
                return new NotificationHistoryOutcome.InvalidCursor(
                    "O cursor aponta para fora da janela pedida. Repita a janela da primeira página.");
            }

            position = decoded;
        }

        var pageSize = request.Limit ?? NotificationQueryContract.DefaultPageSize;
        List<Row> rows = await BuildQuery(request, window, position)
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Take(pageSize + 1)
            .Select(notification => new Row(
                notification.Id,
                notification.Application,
                notification.RecipientId,
                notification.Class,
                notification.Status,
                notification.TemplateKey,
                notification.TemplateVersion,
                notification.CorrelationId,
                notification.CreatedAt))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        List<Row> visible = hasMore ? [.. rows.Take(pageSize)] : rows;

        return new NotificationHistoryOutcome.Page(new NotificationHistoryPage
        {
            Items = [.. visible.Select(ToItem)],
            NextCursor = hasMore
                ? NotificationQueryCursor.Encode(
                    new NotificationQueryPosition(visible[^1].CreatedAt, visible[^1].Id))
                : null,
            Window = window,
        });
    }

    private IQueryable<Notification> BuildQuery(
        NotificationHistoryRequest request,
        NotificationQueryWindow window,
        NotificationQueryPosition? position)
    {
        DateTimeOffset from = window.From;
        DateTimeOffset to = window.To;
        IQueryable<Notification> notifications = db.Notifications
            .Where(notification => notification.CreatedAt >= from && notification.CreatedAt <= to);

        if (request.RecipientId is { } recipientId)
        {
            notifications = notifications.Where(notification => notification.RecipientId == recipientId);
        }

        if (request.CorrelationId is { } correlationId)
        {
            notifications = notifications.Where(notification => notification.CorrelationId == correlationId);
        }

        if (!string.IsNullOrWhiteSpace(request.Class))
        {
            var canonicalClass = request.Class;
            notifications = notifications.Where(notification => notification.Class == canonicalClass);
        }

        if (position is { } resume)
        {
            // PostgreSQL row-value comparison: one predicate over the whole
            // keyset, which the planner serves from the same index that orders
            // the page.
            DateTimeOffset createdAt = resume.CreatedAt;
            Guid id = resume.Id;
            notifications = notifications.Where(notification => EF.Functions.LessThan(
                ValueTuple.Create(notification.CreatedAt, notification.Id),
                ValueTuple.Create(createdAt, id)));
        }

        return notifications;
    }

    private static NotificationHistoryItem ToItem(Row row) => new()
    {
        Id = NotificationId.Format(row.Id),
        Application = row.Application,
        RecipientId = row.RecipientId,
        Class = row.Class,
        Status = row.Status,
        TemplateKey = row.TemplateKey,
        TemplateVersion = row.TemplateVersion,
        CorrelationId = row.CorrelationId,
        CreatedAt = row.CreatedAt,
    };
}
