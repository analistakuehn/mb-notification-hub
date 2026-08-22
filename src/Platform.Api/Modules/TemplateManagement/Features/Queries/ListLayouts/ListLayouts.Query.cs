namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class ListLayouts
{
    /// <summary>Query-string filters plus cursor pagination inputs.</summary>
    internal sealed record Query(
        string? Status,
        string? Owner,
        string? Cursor,
        int? Limit);
}
