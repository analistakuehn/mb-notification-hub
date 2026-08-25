namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

internal static partial class PublishedTemplateRendererLogger
{
    [LoggerMessage(EventId = 2110, Level = LogLevel.Error, Message = "Alarme: a renderização SMS do template {TemplateKey} da aplicação {Application}, versão {Version}, produziu um link em conteúdo de autenticação e foi recusada.")]
    internal static partial void AuthenticationSmsLinkRefused(
        this ILogger logger,
        string application,
        string templateKey,
        int version);
}
