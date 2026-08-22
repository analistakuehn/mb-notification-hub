namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateTemplateLogger
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Template {TemplateKey} criado para a aplicação {Application} com a classe {Class}.")]
    internal static partial void TemplateCreated(this ILogger logger, string templateKey, string application, string @class);
}
