namespace NotificationHub.Api.Modules.Notifications.Features.History;

internal static partial class ListRecipientNotifications
{
    /// <summary>
    /// Subject, filters and paging inputs of the recipient history. The
    /// recipient identity comes from the route and is always exact: there is
    /// no prefix, no pattern and no listing without a subject.
    /// </summary>
    internal sealed record Query(
        string RecipientId,
        string? Class,
        DateTimeOffset? From,
        DateTimeOffset? To,
        string? Cursor,
        int? Limit);
}
