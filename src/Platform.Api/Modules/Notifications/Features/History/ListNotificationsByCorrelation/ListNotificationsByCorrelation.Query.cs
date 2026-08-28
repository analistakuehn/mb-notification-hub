namespace NotificationHub.Api.Modules.Notifications.Features.History;

internal static partial class ListNotificationsByCorrelation
{
    /// <summary>
    /// Subject, filters and paging inputs of the correlation history. The
    /// correlation identity is required and always exact: the route never
    /// lists without a subject, and it never lists by application alone.
    /// </summary>
    internal sealed record Query(
        string? CorrelationId,
        string? Class,
        DateTimeOffset? From,
        DateTimeOffset? To,
        string? Cursor,
        int? Limit);
}
