namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class ValidateLayoutVersionLogger
{
    [LoggerMessage(EventId = 3040, Level = LogLevel.Information, Message = "Versão {Version} do layout {LayoutKey} validada. Aprovada: {Passed}, verificações reprovadas: {FailedChecks}.")]
    internal static partial void LayoutVersionValidated(this ILogger logger, string layoutKey, int version, bool passed, int failedChecks);
}
