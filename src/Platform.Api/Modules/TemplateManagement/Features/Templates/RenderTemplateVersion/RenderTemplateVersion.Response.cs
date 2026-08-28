namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class RenderTemplateVersion
{
    /// <summary>
    /// The rendered preview. The requested locale echoes the input; the
    /// resolved locale is where the fallback chain landed.
    /// </summary>
    internal sealed record Response(
        string Channel,
        string RequestedLocale,
        string ResolvedLocale,
        string? Subject,
        string Body,
        string? BodyText);
}
