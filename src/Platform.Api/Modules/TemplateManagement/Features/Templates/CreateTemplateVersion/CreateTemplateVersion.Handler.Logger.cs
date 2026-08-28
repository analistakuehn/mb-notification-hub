namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class CreateTemplateVersionLogger
{
    [LoggerMessage(EventId = 2010, Level = LogLevel.Information, Message = "Rascunho {Version} do template {TemplateKey} aberto.")]
    internal static partial void DraftOpened(this ILogger logger, string templateKey, int version);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Information, Message = "Rascunho {Version} do template {TemplateKey} aberto como clone da versão {FromVersion}.")]
    internal static partial void DraftCloned(this ILogger logger, string templateKey, int version, int fromVersion);
}
