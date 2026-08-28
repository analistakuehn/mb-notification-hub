namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class PutTemplateVersionVariablesSchemaLogger
{
    [LoggerMessage(EventId = 2030, Level = LogLevel.Information, Message = "Esquema de variáveis do rascunho {Version} do template {TemplateKey} substituído.")]
    internal static partial void VariablesSchemaReplaced(this ILogger logger, string templateKey, int version);
}
