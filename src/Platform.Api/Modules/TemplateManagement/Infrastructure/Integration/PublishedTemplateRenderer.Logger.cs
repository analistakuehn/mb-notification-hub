using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

internal static partial class PublishedTemplateRendererLogger
{
    [LoggerMessage(EventId = 2110, Level = LogLevel.Error, Message = "Alarme: a renderização SMS do template {TemplateKey} da aplicação {Application}, versão {Version}, produziu um link em conteúdo de autenticação e foi recusada.")]
    internal static partial void AuthenticationSmsLinkRefused(
        this ILogger logger,
        string application,
        string templateKey,
        int version);

    [LoggerMessage(EventId = 2111, Level = LogLevel.Warning, Message = "O layout {LayoutKey}, versão {LayoutVersion}, está desativado e a renderização publicada foi recusada.")]
    internal static partial void DisabledLayoutRefused(
        this ILogger logger,
        string layoutKey,
        int layoutVersion);

    [LoggerMessage(EventId = 2112, Level = LogLevel.Warning, Message = "O sandbox recusou o campo {Field} da renderização publicada do template {TemplateKey} da aplicação {Application}, versão {Version}, para ({Channel}, {ResolvedLocale}), no modo {Mode}.")]
    internal static partial void PublishedRenderRefused(
        this ILogger logger,
        string application,
        string templateKey,
        int version,
        string channel,
        string resolvedLocale,
        string field,
        TemplateRefusal mode);
}
