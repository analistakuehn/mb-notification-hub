namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class PutTemplateVersionSensitiveVariablesLogger
{
    // The count and never the names: a name is authored text, and this line
    // lands in a log an operator reads long after the draft is gone.
    [LoggerMessage(EventId = 2130, Level = LogLevel.Information, Message = "Rascunho {Version} do template {TemplateKey} declarou {Count} variáveis sensíveis.")]
    internal static partial void SensitiveVariablesDeclared(this ILogger logger, string templateKey, int version, int count);
}
