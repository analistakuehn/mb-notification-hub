using System.Text.Json;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class RenderTemplateVersion
{
    /// <summary>HTTP body: the (channel, locale) to render and the sample variables to render with.</summary>
    internal sealed record Request(string? Channel, string? Locale, JsonElement? Variables);
}
