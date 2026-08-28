namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class PutTemplateVersionContentLogger
{
    [LoggerMessage(EventId = 2020, Level = LogLevel.Information, Message = "Conteúdo do rascunho {Version} do template {TemplateKey} atualizado para ({Channel}, {Locale}).")]
    internal static partial void ContentUpdated(this ILogger logger, string templateKey, int version, string channel, string locale);
}
