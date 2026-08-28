namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class DeprecateTemplateLogger
{
    [LoggerMessage(EventId = 2070, Level = LogLevel.Information, Message = "Template {TemplateKey} depreciado; novas solicitações passam a ser rejeitadas.")]
    internal static partial void TemplateDeprecated(this ILogger logger, string templateKey);
}
