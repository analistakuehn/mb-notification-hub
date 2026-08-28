namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class ListTemplates
{
    /// <summary>Query-string filters plus cursor pagination inputs.</summary>
    internal sealed record Query(
        string? Application,
        string? Class,
        string? Status,
        string? Owner,
        string? Cursor,
        int? Limit);
}
