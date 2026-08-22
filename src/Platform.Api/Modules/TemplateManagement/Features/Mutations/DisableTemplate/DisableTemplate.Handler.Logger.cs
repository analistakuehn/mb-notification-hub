namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class DisableTemplateLogger
{
    [LoggerMessage(EventId = 2080, Level = LogLevel.Information, Message = "Template {TemplateKey} desabilitado; estado terminal, sem retorno pela API.")]
    internal static partial void TemplateDisabled(this ILogger logger, string templateKey);
}
