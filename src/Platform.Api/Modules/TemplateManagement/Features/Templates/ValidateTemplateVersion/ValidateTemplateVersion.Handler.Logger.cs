namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class ValidateTemplateVersionLogger
{
    [LoggerMessage(EventId = 2040, Level = LogLevel.Information, Message = "Versão {Version} do template {TemplateKey} validada. Aprovada: {Passed}, verificações reprovadas: {FailedChecks}.")]
    internal static partial void VersionValidated(this ILogger logger, string templateKey, int version, bool passed, int failedChecks);
}
