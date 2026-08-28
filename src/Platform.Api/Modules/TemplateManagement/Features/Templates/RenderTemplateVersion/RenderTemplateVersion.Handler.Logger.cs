namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class RenderTemplateVersionLogger
{
    [LoggerMessage(EventId = 2050, Level = LogLevel.Information, Message = "Versão {Version} do template {TemplateKey} renderizada para ({Channel}, {ResolvedLocale}).")]
    internal static partial void VersionRendered(this ILogger logger, string templateKey, int version, string channel, string resolvedLocale);
}
