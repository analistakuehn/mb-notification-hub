using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class RenderTemplateVersionLogger
{
    [LoggerMessage(EventId = 2050, Level = LogLevel.Information, Message = "Versão {Version} do template {TemplateKey} renderizada para ({Channel}, {ResolvedLocale}).")]
    internal static partial void VersionRendered(this ILogger logger, string templateKey, int version, string channel, string resolvedLocale);

    [LoggerMessage(EventId = 2051, Level = LogLevel.Information, Message = "O sandbox recusou o campo {Field} da prévia da versão {Version} do template {TemplateKey} da aplicação {Application}, para ({Channel}, {ResolvedLocale}), no modo {Mode}.")]
    internal static partial void PreviewRenderRefused(
        this ILogger logger,
        string application,
        string templateKey,
        int version,
        string channel,
        string resolvedLocale,
        string field,
        TemplateRefusal mode);
}
